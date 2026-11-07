using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AIAnnounce;

// Spots a session that was working and has settled idle: its turn is over and it waits for your next
// prompt. It has to stay idle a few seconds, so a dip between steps doesn't count, and it has to have
// worked a few seconds, so a background notice that wakes it briefly doesn't either. A turn that ends
// on a question or stops at a permission prompt has the needs-you alert instead.
sealed class FinishWatch
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinWork = TimeSpan.FromSeconds(5);

    public sealed record Finished(SessionView Session, TimeSpan Worked);

    sealed class Track
    {
        public DateTime? WorkStart, IdleAt;
    }

    readonly Dictionary<string, Track> tracks = new();

    // Call with every snapshot; returns the sessions whose turn ended since the last call.
    public List<Finished> Update(IReadOnlyList<SessionView> sessions, DateTime now)
    {
        var done = new List<Finished>();
        var seen = new HashSet<string>();
        foreach (var s in sessions)
        {
            string key = $"{s.Source}|{s.Pid}|{s.ProcStart}|{s.SessionId}";
            seen.Add(key);
            if (!tracks.TryGetValue(key, out var t)) tracks[key] = t = new Track();
            switch (s.State)
            {
                case SessionState.Busy:
                    // a stop at a permission prompt mid-turn doesn't restart the clock
                    t.WorkStart ??= s.Since > DateTime.MinValue && s.Since <= now ? s.Since : now;
                    t.IdleAt = null;
                    break;
                case SessionState.Idle when t.WorkStart is DateTime start:
                    t.IdleAt ??= now;
                    if (now - t.IdleAt.Value < Settle) break;
                    var worked = t.IdleAt.Value - start;
                    if (worked >= MinWork) done.Add(new Finished(s, worked));
                    t.WorkStart = t.IdleAt = null;
                    break;
                case SessionState.Waiting:
                    t.IdleAt = null;
                    break;
                case SessionState.Asked:
                    t.WorkStart = t.IdleAt = null;
                    break;
            }
        }
        foreach (string gone in tracks.Keys.Where(k => !seen.Contains(k)).ToList()) tracks.Remove(gone);
        return done;
    }
}

// The finish sound, made here rather than borrowed from Windows: a soft rising two-note chime, G5 then
// C6. Windows maps Exclamation (the needs-you sound), Asterisk and the default beep to the same file,
// so no other system sound would tell the two alerts apart, and this one can't be mistaken for another
// app's notification either.
static class Chime
{
    const int Rate = 44100;
    static SoundPlayer? player;  // kept for the life of the app: PlaySound reads its buffer while playing

    public static void Play()
    {
        try
        {
            player ??= Load();
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            Log.Write("chime: " + ex.Message);
        }
    }

    static SoundPlayer Load()
    {
        var p = new SoundPlayer(new MemoryStream(Wave()));
        p.Load();
        return p;
    }

    // 16-bit mono PCM. Each note is a fundamental with a little second and third harmonic, a 5 ms
    // attack and an exponential fade; the second note starts 130 ms after the first.
    internal static byte[] Wave()
    {
        double[] notes = { 783.99, 1046.50 }, starts = { 0, 0.13 };
        int length = (int)(Rate * 0.75);
        var samples = new double[length];
        for (int n = 0; n < notes.Length; n++)
            for (int i = (int)(starts[n] * Rate); i < length; i++)
            {
                double t = i / (double)Rate - starts[n];
                double w = 2 * Math.PI * notes[n] * t;
                samples[i] += Math.Min(1, t / 0.005) * Math.Exp(-t / 0.16) * (Math.Sin(w) + 0.25 * Math.Sin(2 * w) + 0.08 * Math.Sin(3 * w));
            }
        double peak = samples.Max(x => Math.Abs(x));
        int fade = Rate / 50;  // the last 20 ms ramp to zero, so the end doesn't click

        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + length * 2);
            w.Write("WAVEfmt "u8);
            w.Write(16);
            w.Write((short)1);  // PCM
            w.Write((short)1);  // mono
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(length * 2);
            for (int i = 0; i < length; i++)
                w.Write((short)Math.Round(samples[i] / peak * 0.45 * Math.Min(1, (length - i) / (double)fade) * short.MaxValue));
        }
        return stream.ToArray();
    }
}

// WinForms' ShowBalloonTip can't ask for a silent notification, and Windows plays its notification
// sound with every one. This sends the same NIM_MODIFY plus NIIF_NOSOUND through the icon's own window,
// so clicking the notification still raises BalloonTipClicked. If a WinForms update renames the two
// fields it reads, it falls back to ShowBalloonTip, sound and all.
static class TrayBalloon
{
    const uint NIM_MODIFY = 1, NIF_INFO = 0x10, NIIF_NOSOUND = 0x10;

    static readonly FieldInfo? WindowField = typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? IdField = typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool CanSilence =>
        WindowField != null && typeof(NativeWindow).IsAssignableFrom(WindowField.FieldType) && IdField?.FieldType == typeof(uint);

    public static void ShowSilent(NotifyIcon icon, string title, string text)
    {
        if (CanSilence && WindowField!.GetValue(icon) is NativeWindow window && window.Handle != IntPtr.Zero)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = window.Handle,
                uID = (uint)IdField!.GetValue(icon)!,
                uFlags = NIF_INFO,
                szTip = "",
                szInfo = text,
                szInfoTitle = title,
                dwInfoFlags = NIIF_NOSOUND,
            };
            if (Shell_NotifyIconW(NIM_MODIFY, ref data)) return;
        }
        icon.ShowBalloonTip(8000, title, text, ToolTipIcon.None);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
}
