using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AIAnnounce;

static class Palette
{
    public static readonly Color Background = Color.FromArgb(22, 22, 26);
    public static readonly Color Header = Color.FromArgb(31, 31, 37);
    public static readonly Color Border = Color.FromArgb(62, 62, 72);
    public static readonly Color Hover = Color.FromArgb(42, 42, 50);
    public static readonly Color Text = Color.FromArgb(236, 236, 241);
    public static readonly Color Soft = Color.FromArgb(200, 200, 209);
    public static readonly Color Dim = Color.FromArgb(142, 142, 154);
    public static readonly Color Faint = Color.FromArgb(100, 100, 112);
    public static readonly Color Accent = Color.FromArgb(128, 201, 235);
    public static readonly Color Amber = Color.FromArgb(245, 184, 61);
    public static readonly Color AmberText = Color.FromArgb(252, 210, 128);
    public static readonly Color Agent = Color.FromArgb(167, 139, 250);
    public static readonly Color Shell = Color.FromArgb(94, 196, 182);
    public static readonly Color BgShell = Color.FromArgb(108, 168, 240);
    public static readonly Color GpuOk = Color.FromArgb(118, 185, 0);
    public static readonly Color GpuHot = Color.FromArgb(235, 87, 87);
    public static readonly Color Codex = Color.FromArgb(214, 214, 224);
    public static readonly Color Agy = Color.FromArgb(129, 212, 140);
}

sealed class OverlayFonts : IDisposable
{
    static readonly string MonoFamily =
        FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";

    public OverlayFonts(float scale)
    {
        Title = new Font("Segoe UI Semibold", 13f * scale, GraphicsUnit.Pixel);
        Name = new Font("Segoe UI Semibold", 12.5f * scale, GraphicsUnit.Pixel);
        Small = new Font("Segoe UI", 11.5f * scale, GraphicsUnit.Pixel);
        Mono = new Font(MonoFamily, 11f * scale, GraphicsUnit.Pixel);
        Glyph = new Font("Segoe UI Symbol", 14f * scale, GraphicsUnit.Pixel);
        Emoji = new Font("Segoe UI Emoji", 13f * scale, GraphicsUnit.Pixel);
        Tag = new Font("Segoe UI Semibold", 9.5f * scale, GraphicsUnit.Pixel);
    }

    public Font Title { get; }
    public Font Name { get; }
    public Font Small { get; }
    public Font Mono { get; }
    public Font Glyph { get; }
    public Font Emoji { get; }
    public Font Tag { get; }

    public void Dispose()
    {
        foreach (var f in new[] { Title, Name, Small, Mono, Glyph, Emoji, Tag }) f.Dispose();
    }
}

sealed class OverlayForm : Form
{
    enum RowKind { Header, Usage, Session, Attention, Activity, Item, Summary, Notice, Empty, Footer }

    sealed record Row(RowKind Kind, Rectangle Bounds, SessionView? Session = null, ItemView? Item = null, string? Text = null, UsageInfo? Usage = null);

    const int WidthDip = 460;
    const int HeaderDip = 34, UsageDip = 30, SessionDip = 26, LineDip = 20, FooterDip = 22;

    // Claude Code's own spinner frames
    static readonly string[] Spinner = { "·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢" };

    const TextFormatFlags LeftText = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                     TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
    const TextFormatFlags CenterText = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    readonly Settings settings;
    readonly SessionMonitor? monitor;
    readonly UsageMonitor? usageMonitor;
    readonly NotifyIcon? tray;
    readonly Icon? idleIcon, alertIcon;
    TaskbarButton? taskbarButton;
    IReadOnlyList<UsageInfo> usage = Array.Empty<UsageInfo>();
    readonly SessionPause? pause;
    readonly bool previewPaused;
    bool hoverPause;
    Rectangle pauseButton;
    readonly System.Windows.Forms.Timer animation = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer housekeeping = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer hoverDelay = new() { Interval = 450 };
    readonly ToolTip tip = new() { UseAnimation = false, UseFading = false };
    readonly List<Row> rows = new();
    readonly HashSet<string> announced = new();
    readonly FinishWatch finishes = new();
    readonly Dictionary<int, DateTime> quietUntil = new();  // pids the emergency stop just halted
    readonly HashSet<string> dismissed = new();
    readonly Dictionary<(string, Font), int> measured = new();
    RegisteredWaitHandle? showWait;
    IVirtualDesktopManager? desktops;
    OverlayFonts fonts;
    Snapshot snapshot = Snapshot.Empty;
    float scale;
    bool firstSnapshot = true;
    int ticks, hover = -1;
    bool hoverMenu, hoverStop, pressed, dragging, trayAlert, pulsing, closingButton;
    int hoverRestartPid;
    Rectangle menuButton, alertPill, stopButton, paintClip;
    DateTime stopArmedUntil, stoppingUntil;
    readonly HashSet<int> restarting = new();  // pids we've asked to exit and resume
    Point lastMouse, pressAt;
    DateTime flashUntil;
    SessionView? toastSession;

    public OverlayForm(string claudeDir, EventWaitHandle? showSignal, Snapshot? fixedSnapshot = null, IReadOnlyList<UsageInfo>? fixedUsage = null, bool previewPaused = false)
    {
        settings = fixedSnapshot == null ? Settings.Load() : new Settings();
        this.previewPaused = previewPaused;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        // true avoids WinForms' hidden owner window; the taskbar button is removed in CreateParams
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.Background;
        Text = "AI Announce";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        scale = DeviceDpi / 96f;
        fonts = new OverlayFonts(scale);

        if (fixedSnapshot != null)
        {
            snapshot = fixedSnapshot;
            usage = fixedUsage ?? Array.Empty<UsageInfo>();
            LayoutRows();
            return;
        }

        Opacity = Math.Clamp(settings.Opacity, 0.4, 1.0);
        idleIcon = BrandIcons.AppIcon();
        Icon = idleIcon;
        alertIcon = TrayIcon(Palette.Amber, "✋", "Segoe UI Emoji");
        tray = new NotifyIcon { Icon = idleIcon, Text = "AI Announce", Visible = true, ContextMenuStrip = new TopmostMenu() };
        tray.ContextMenuStrip.Opening += (_, e) =>
        {
            var items = tray.ContextMenuStrip.Items;
            items.Clear();
            BuildTrayMenu(items);
            e.Cancel = false;
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) SetOverlayVisible(!Visible); };
        tray.BalloonTipClicked += (_, _) => { if (toastSession != null) TerminalFocus.Focus(toastSession.Pid, toastSession.TabTitles); };

        monitor = new SessionMonitor(claudeDir);
        SessionView? Current(SessionView expected) => monitor.Latest.Sessions.FirstOrDefault(s => s.Pid == expected.Pid && s.ProcStart == expected.ProcStart);
        pause = new SessionPause(Current,
            (target, keys) => System.Threading.Tasks.Task.Run(() => ConsoleInput.SendSession(target, () => Current(target), keys)),
            Path.Combine(Settings.Dir, "paused-sessions.json"));
        pause.Changed += () => { LayoutRows(); Invalidate(); UpdateTray(); UpdateTaskbar(); };
        monitor.Updated += s => Post(() => Apply(s));
        usageMonitor = new UsageMonitor(claudeDir);
        // a refresh already in flight when Plan usage was switched off must not bring the strip back
        usageMonitor.Updated += u => Post(() => { if (settings.PlanUsage) ApplyUsage(u); });

        animation.Tick += (_, _) => InvalidateAnimated();
        housekeeping.Tick += (_, _) => Housekeeping();
        hoverDelay.Tick += (_, _) => ShowTip();
        if (showSignal != null)
            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
                (_, _) => BeginInvoke(new Action(() => SetOverlayVisible(true))), null, -1, false);
        LayoutRows();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Tool windows have no taskbar/Alt-Tab entry and are not pinned to a virtual desktop
            cp.ExStyle = (cp.ExStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST) & ~Native.WS_EX_APPWINDOW;
            cp.ClassStyle |= Native.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1, round = 2, border = ColorTranslator.ToWin32(Palette.Border);
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        try { desktops = (IVirtualDesktopManager)new VirtualDesktopManager(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Write("virtual desktops: " + ex.Message); }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (monitor == null) return;
        PlaceInitially();
        monitor.Start();
        if (settings.PlanUsage) usageMonitor?.Start();
        housekeeping.Start();
        if (settings.TaskbarButton) SetTaskbarButton(true);
        if (settings.Hidden) BeginInvoke(new Action(Hide));
    }

    // Worker threads report in here; drop the update if the window is already gone.
    void Post(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (desktops == null) return;
        int hrId = desktops.GetWindowDesktopId(Handle, out Guid id);
        int hrOn = desktops.IsWindowOnCurrentVirtualDesktop(Handle, out int onCurrent);
        Log.Write($"started; desktop id hr=0x{hrId:X8} {id}, on current hr=0x{hrOn:X8} {onCurrent}");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        housekeeping.Stop();
        animation.Stop();
        hoverDelay.Stop();
        showWait?.Unregister(null);
        monitor?.Dispose();
        usageMonitor?.Dispose();
        taskbarButton?.Dispose();
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        base.OnFormClosed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        scale = e.DeviceDpiNew / 96f;
        fonts.Dispose();
        fonts = new OverlayFonts(scale);
        measured.Clear();
        LayoutRows();
        Invalidate();
    }

    // ---- state -------------------------------------------------------------------------------

    void Apply(Snapshot s)
    {
        snapshot = s;
        _ = pause?.MaintainAsync();
        restarting.IntersectWith(s.Sessions.Select(x => x.Pid));  // finished ones are gone
        Announce(s);
        LayoutRows();
        Invalidate();
        // the pulse is reserved for sessions that are blocked; "asked" stays steady
        pulsing = s.Sessions.Any(x => IsAlert(x) && x.State == SessionState.Waiting);
        animation.Interval = pulsing ? 80 : 120;
        animation.Enabled = Visible && (pulsing || s.Sessions.Any(x => x.State == SessionState.Busy));
        UpdateTray();
        UpdateTaskbar();
    }

    void ApplyUsage(IReadOnlyList<UsageInfo> u)
    {
        usage = u;
        UsageDiagnostics.Save(u);
        LayoutRows();
        Invalidate();
        UpdateTray();
        UpdateTaskbar();
    }

    // The limit that binds first: the session window, unless a weekly one is further along.
    UsageLimit? Headline() => usage.SelectMany(u => u.Limits).Where(l => l.Available && (l.ResetsAt == null || l.ResetsAt > DateTime.UtcNow)).OrderByDescending(l => l.Percent).FirstOrDefault();

    string StatusLine()
    {
        int alerts = snapshot.Sessions.Count(IsAlert);
        int busy = snapshot.Sessions.Count(x => x.State == SessionState.Busy);
        var parts = new List<string>();
        foreach (var u in usage)
            if (u.Limits.Any(l => l.Available)) parts.Add(u.DisplayName + " " + string.Join(" / ", u.Limits.Take(2).Select(l => l.Available ? $"{l.Label} {l.Percent:0}%" : $"{l.Label} —")));
        if (PausedCount > 0) parts.Add($"{PausedCount} paused");
        parts.Add($"{busy}/{snapshot.Sessions.Count} working");
        if (alerts > 0) parts.Add($"{alerts} need{(alerts == 1 ? "s" : "")} you");
        return string.Join(" · ", parts);
    }

    void UpdateTaskbar()
    {
        if (taskbarButton == null) return;
        int alerts = snapshot.Sessions.Count(IsAlert);
        taskbarButton.SetStatus($"AI Announce · {StatusLine()}", Headline(),
            alerts > 0 ? alertIcon : null, alerts > 0 ? $"{alerts} session(s) need you" : null);
    }

    void SetTaskbarButton(bool on)
    {
        if (on && taskbarButton == null)
        {
            taskbarButton = new TaskbarButton(idleIcon ?? SystemIcons.Application, () => SetOverlayVisible(!Visible), RenderThumbnail);
            taskbarButton.FormClosed += (_, _) =>
            {
                // "Close window" on the taskbar button quits the app; turning the button off just drops it
                if (taskbarButton != null && !closingButton) Close();
            };
            taskbarButton.Reveal();
            UpdateTaskbar();
        }
        else if (!on && taskbarButton != null)
        {
            closingButton = true;
            taskbarButton.Close();
            taskbarButton = null;
            closingButton = false;
        }
    }

    // Only the spinner glyphs, the pulsing rows and the header pill change between snapshots.
    void InvalidateAnimated()
    {
        foreach (var row in rows)
        {
            if (row.Session is not SessionView s) continue;
            bool alert = IsAlert(s);
            if (alert && s.State == SessionState.Waiting)
                Invalidate(row.Bounds);
            else if (!alert && row.Kind == RowKind.Session && s.State == SessionState.Busy)
                Invalidate(new Rectangle(row.Bounds.Left, row.Bounds.Top, Px(34), row.Bounds.Height));
        }
        if (pulsing && !alertPill.IsEmpty) Invalidate(alertPill);
    }

    bool IsAlert(SessionView s) => s.NeedsUser && (s.State == SessionState.Waiting || !IsPaused(s)) && !dismissed.Contains(s.AttentionKey);

    void Announce(Snapshot s)
    {
        var active = new HashSet<string>();
        foreach (var sv in s.Sessions.Where(x => x.NeedsUser))
        {
            string key = sv.AttentionKey;
            active.Add(key);
            if (!announced.Add(key) || firstSnapshot) continue;
            flashUntil = DateTime.UtcNow.AddSeconds(4);
            // no toast for the session you're already looking at
            bool watching = TerminalFocus.IsForeground(sv.TabTitles);
            Log.Write($"alert: {sv.Title} [{sv.AttentionKind}] {sv.Attention} " +
                      (watching ? "(terminal focused, no toast)" : settings.Notifications ? "(toast)" : "(toasts off)"));
            if (!settings.Notifications || watching) continue;
            toastSession = sv;
            string title = sv.State == SessionState.Asked ? $"✋ {sv.Title} asked you something" : $"✋ {sv.Title} needs you";
            tray?.ShowBalloonTip(8000, ToolText.Clip(title, 63), ToolText.Clip(sv.Attention ?? "Waiting for your input", 250), ToolTipIcon.None);
            if (settings.Sound) SystemSounds.Exclamation.Play();
        }
        announced.IntersectWith(active);
        dismissed.IntersectWith(active);
        firstSnapshot = false;
        AnnounceFinished(s);
    }

    // A working session settled idle: its turn is done. Same rule as the needs-you alert (no toast for
    // the terminal you're looking at), but quieter: the toast itself is silent, and the finish chime,
    // when it's on, is a sound of its own.
    void AnnounceFinished(Snapshot s)
    {
        DateTime now = DateTime.UtcNow;
        foreach (int pid in quietUntil.Where(q => q.Value <= now).Select(q => q.Key).ToList()) quietUntil.Remove(pid);
        foreach (var (sv, worked) in finishes.Update(s.Sessions, now))
        {
            // you stopped it from here; announcing that back would be noise
            if (IsPaused(sv) || quietUntil.ContainsKey(sv.Pid)) continue;
            bool watching = TerminalFocus.IsForeground(sv.TabTitles);
            Log.Write($"finished: {sv.Title} after {Ago(worked)} " + (watching ? "(terminal focused, no toast)"
                : !settings.FinishedNotify ? "(toasts off)" : settings.FinishedSound ? "(toast, chime)" : "(silent toast)"));
            if (!settings.FinishedNotify || watching || tray == null) continue;
            toastSession = sv;
            string project = string.Equals(sv.Project, sv.Title, StringComparison.OrdinalIgnoreCase) ? "" : sv.Project;
            TrayBalloon.ShowSilent(tray, ToolText.Clip($"✔ {sv.Title} finished", 63),
                ToolText.Clip(JoinParts(sv.Product, project, $"worked {Ago(worked)}") ?? "", 250));
            if (settings.FinishedSound) Chime.Play();
        }
    }

    void UpdateTray()
    {
        if (tray == null) return;
        int alerts = snapshot.Sessions.Count(IsAlert);
        int busy = snapshot.Sessions.Count(x => x.State == SessionState.Busy);
        if (trayAlert != alerts > 0)
        {
            trayAlert = alerts > 0;
            tray.Icon = trayAlert ? alertIcon : idleIcon;
        }
        string text = "AI Announce: " + StatusLine();
        if (tray.Text != text) tray.Text = ToolText.Clip(text, 127);
    }

    void Housekeeping()
    {
        if (IsDisposed || Disposing) return;
        ticks++;
        if (stopArmedUntil != DateTime.MinValue && DateTime.UtcNow >= stopArmedUntil)
        {
            stopArmedUntil = DateTime.MinValue;  // disarm quietly
            Invalidate();
        }
        if (ticks % 15 == 0)
            foreach (var row in rows.Where(r => r.Kind == RowKind.Usage)) Invalidate(row.Bounds);
        // The taskbar button belongs to whichever desktop its window is on, so carry it along
        // when you switch. The overlay is a tool window and isn't tied to a desktop to begin with.
        if (taskbarButton is { IsDisposed: false, Disposing: false, IsHandleCreated: true }) KeepOnCurrentDesktop(taskbarButton.Handle);
        if (!Visible) return;
        KeepOnCurrentDesktop(Handle);
        if (ticks % 5 == 0)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            TopmostMenu.RaiseOpenMenus();
        }
    }

    void KeepOnCurrentDesktop(IntPtr window)
    {
        if (desktops == null) return;
        try
        {
            if (desktops.IsWindowOnCurrentVirtualDesktop(window, out int on) != 0 || on != 0) return;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || desktops.GetWindowDesktopId(fg, out Guid id) != 0 || id == Guid.Empty) return;
            desktops.MoveWindowToDesktop(window, ref id);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            desktops = null;
        }
    }

    // ---- layout ------------------------------------------------------------------------------

    void LayoutRows()
    {
        int height = BuildRows(rows, settings.Collapsed, settings.HideIdle, (int)(Screen.FromControl(this).WorkingArea.Height * 0.85));
        ApplySize(Px(WidthDip), height);
    }

    // Lays out header, usage strip and sessions into `into`; returns the total height.
    int BuildRows(List<Row> into, bool collapsed, bool hideIdle, int maxHeight)
    {
        into.Clear();
        int width = Px(WidthDip);
        into.Add(new Row(RowKind.Header, new Rectangle(0, 0, width, Px(HeaderDip))));
        int y = Px(HeaderDip);
        int Finish()
        {
            into.Add(new Row(RowKind.Footer, new Rectangle(0, y, width, Px(FooterDip))));
            return y + Px(FooterDip);
        }
        foreach (var provider in usage)
        {
            into.Add(new Row(RowKind.Usage, new Rectangle(0, y, width, Px(UsageDip)), Usage: provider));
            y += Px(UsageDip);
        }
        if (pause?.Error is string error)
        {
            into.Add(new Row(RowKind.Notice, new Rectangle(0, y, width, Px(24)), Text: error));
            y += Px(24);
        }
        if (collapsed) return Finish();

        y += Px(4);
        // needs-you first, then working, then idle; start order within each group (OrderBy is stable)
        var all = snapshot.Sessions
            .OrderBy(s => IsAlert(s) ? (s.State == SessionState.Waiting ? 0 : 1) : s.State == SessionState.Busy ? 2 : 3)
            .ToList();
        if (all.Count == 0)
        {
            into.Add(new Row(RowKind.Empty, new Rectangle(0, y, width, Px(28))));
            y += Px(28);
        }

        int idleHidden = 0, overflow = 0;
        foreach (var s in all)
        {
            bool alert = IsAlert(s);
            if (hideIdle && !IsPaused(s) && s.State == SessionState.Idle && s.Items.Count == 0 && !alert)
            {
                idleHidden++;
                continue;
            }
            int need = Px(SessionDip) + (alert && s.Attention != null ? Px(LineDip) : 0) +
                       (s.Activity != null ? Px(LineDip) : 0) + s.Items.Count * Px(LineDip);
            if (y + need > maxHeight - Px(30 + FooterDip))
            {
                overflow++;
                continue;
            }

            into.Add(new Row(RowKind.Session, new Rectangle(0, y, width, Px(SessionDip)), s));
            y += Px(SessionDip);
            if (alert && s.Attention != null)
            {
                into.Add(new Row(RowKind.Attention, new Rectangle(0, y, width, Px(LineDip)), s));
                y += Px(LineDip);
            }
            if (s.Activity != null)
            {
                into.Add(new Row(RowKind.Activity, new Rectangle(0, y, width, Px(LineDip)), s));
                y += Px(LineDip);
            }
            foreach (var item in s.Items)
            {
                into.Add(new Row(RowKind.Item, new Rectangle(0, y, width, Px(LineDip)), s, item));
                y += Px(LineDip);
            }
        }

        if (idleHidden + overflow > 0)
        {
            var parts = new List<string>();
            if (idleHidden > 0) parts.Add($"{idleHidden} idle hidden");
            if (overflow > 0) parts.Add($"{overflow} more (no room)");
            into.Add(new Row(RowKind.Summary, new Rectangle(0, y, width, Px(22)), Text: "+ " + string.Join(" · ", parts)));
            y += Px(22);
        }
        y += Px(6);
        return Finish();
    }

    void ApplySize(int width, int height)
    {
        if (ClientSize.Width == width && ClientSize.Height == height) return;
        if (!IsHandleCreated || !Visible)
        {
            ClientSize = new Size(width, height);
            return;
        }
        // in the lower half of the screen, grow upwards so the bottom edge stays put
        var wa = Screen.FromControl(this).WorkingArea;
        bool anchorBottom = Top + Height / 2 > wa.Top + wa.Height / 2;
        int top = anchorBottom ? Bottom - height : Top;
        top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - height));
        SetBounds(Left, top, width, height);
    }

    void PlaceInitially()
    {
        if (settings.X is int x && settings.Y is int y &&
            Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(new Point(x + Px(40), y + Px(10)))))
        {
            Location = new Point(x, y);
            return;
        }
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(wa.Right - Width - Px(16), wa.Top + Px(16));
    }

    // ---- painting ----------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) => PaintAll(e.Graphics, e.ClipRectangle);

    public void RenderTo(string path)
    {
        LayoutRows();
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp)) PaintAll(g, new Rectangle(Point.Empty, ClientSize));
        bmp.Save(path, ImageFormat.Png);
    }

    // Taskbar hover thumbnail: header, usage and only the sessions doing something, scaled to fit.
    Bitmap? RenderThumbnail(Size max)
    {
        if (max.Width <= 0 || max.Height <= 0) return null;
        var list = new List<Row>();
        int width = Px(WidthDip);
        int height = BuildRows(list, collapsed: false, hideIdle: true, maxHeight: Px(900));
        // GDI text leaves alpha undefined on ARGB surfaces; paint opaque first, convert after
        using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full)) PaintRows(g, new Rectangle(0, 0, width, height), list, -1, full.Size);
        float k = Math.Min(1f, Math.Min((float)max.Width / width, (float)max.Height / height));
        var result = new Bitmap(Math.Max(1, (int)(width * k)), Math.Max(1, (int)(height * k)), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, result.Width, result.Height);
        }
        return result;
    }

    void PaintAll(Graphics g, Rectangle clip) => PaintRows(g, clip, rows, pressed ? -1 : hover, ClientSize);

    void PaintRows(Graphics g, Rectangle clip, List<Row> list, int hoverIndex, Size size)
    {
        paintClip = clip;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Fill(g, clip, Palette.Background);
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < list.Count; i++)
        {
            var row = list[i];
            if (!row.Bounds.IntersectsWith(clip)) continue;
            bool hovered = i == hoverIndex;
            switch (row.Kind)
            {
                case RowKind.Header: PaintHeader(g, row.Bounds); break;
                case RowKind.Usage: PaintUsage(g, row, hovered); break;
                case RowKind.Notice:
                    Draw(g, row.Text ?? "", fonts.Small, Inset(row.Bounds, Px(10), Px(10)), Palette.Amber);
                    break;
                case RowKind.Session: PaintSession(g, row, hovered, now); break;
                case RowKind.Attention: PaintAttention(g, row); break;
                case RowKind.Activity: PaintActivity(g, row, now); break;
                case RowKind.Item: PaintItem(g, row, hovered, now); break;
                case RowKind.Summary:
                    if (hovered) Fill(g, row.Bounds, Palette.Hover);
                    Draw(g, row.Text ?? "", fonts.Small, Inset(row.Bounds, Px(14), Px(10)), Palette.Faint);
                    break;
                case RowKind.Empty:
                    Draw(g, "No Claude Code, Codex or Gemini (agy) sessions running", fonts.Small, Inset(row.Bounds, Px(14), Px(10)), Palette.Faint);
                    break;
                case RowKind.Footer:
                    Fill(g, new Rectangle(row.Bounds.Left, row.Bounds.Top, row.Bounds.Width, 1), Palette.Border);
                    Draw(g, "AI Announce v" + AppVersion.Current, fonts.Small, Inset(row.Bounds, Px(10), Px(10)), Palette.Dim,
                        LeftText | TextFormatFlags.Right);
                    break;
            }
        }
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(Palette.Border);
        g.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);
    }

    // Plan usage under the header: session window and weekly window, plus any model-scoped
    // weekly limit once it's worth looking at. Every limit is in the tooltip.
    void PaintUsage(Graphics g, Row row, bool hovered)
    {
        var r = row.Bounds;
        var info = row.Usage!;
        Fill(g, r, hovered ? Palette.Hover : Palette.Header);
        using (var pen = new Pen(Color.FromArgb(30, 255, 255, 255)))
            g.DrawLine(pen, r.Left + Px(10), r.Top, r.Right - Px(10), r.Top);
        BrandIcons.DrawProvider(g, new Rectangle(r.Left + Px(10), r.Top + Px(7), Px(16), Px(16)), info.Provider);
        int left = r.Left + Px(112);
        string label = info.ProviderName;
        Draw(g, label, fonts.Tag, new Rectangle(r.Left + Px(32), r.Top, Px(74), info.Scope == null ? r.Height : Px(16)), BrandIcons.ColorFor(info.Provider));
        if (info.Scope != null)
            Draw(g, info.Scope, fonts.Tag, new Rectangle(r.Left + Px(32), r.Top + Px(14), Px(76), Px(14)), Palette.Dim);
        var shown = UsageParsing.Windows(info.Limits, "No data reported.").Take(2).ToList();
        int cellWidth = (r.Right - Px(10) - left) / 2;
        for (int i = 0; i < shown.Count; i++)
            PaintLimit(g, new Rectangle(left + i * cellWidth, r.Top, cellWidth - Px(12), r.Height), shown[i]);
    }

    void PaintLimit(Graphics g, Rectangle cell, UsageLimit limit)
    {
        Color color = limit.Critical ? Palette.GpuHot : limit.Warning ? Palette.Amber : Palette.Shell;
        int labelWidth = Measure(limit.Label, fonts.Tag);
        Draw(g, limit.Label, fonts.Tag, new Rectangle(cell.Left, cell.Top, labelWidth + 1, cell.Height), Palette.Faint);

        bool expired = limit.ResetsAt <= DateTime.UtcNow;
        if (!limit.Available || expired)
        {
            Draw(g, expired ? "reset passed" : "—", fonts.Small,
                new Rectangle(cell.Left + labelWidth + Px(8), cell.Top, Math.Max(0, cell.Width - labelWidth - Px(8)), cell.Height), Palette.Dim);
            return;
        }
        string percent = $"{limit.Percent:0}%";
        int percentWidth = Measure(percent, fonts.Name);
        string reset = limit.ResetsAt is DateTime at ? Countdown(at - DateTime.UtcNow) : "";
        int resetWidth = reset.Length > 0 ? Measure(reset, fonts.Small) : 0;

        int x = cell.Left + labelWidth + Px(6);
        int room = cell.Right - x - percentWidth - Px(6);
        bool showReset = resetWidth > 0 && room - resetWidth - Px(6) >= Px(28);  // keep the bar readable
        int barWidth = Math.Max(Px(16), room - (showReset ? resetWidth + Px(6) : 0));
        var bar = new Rectangle(x, cell.Top + cell.Height / 2 - Px(3), barWidth, Px(6));
        Bar(g, bar, (float)(limit.Percent / 100.0), color);
        x = bar.Right + Px(6);
        Draw(g, percent, fonts.Name, new Rectangle(x, cell.Top, percentWidth + 1, cell.Height), color);
        if (showReset)
            Draw(g, reset, fonts.Small, new Rectangle(x + percentWidth + Px(6), cell.Top, resetWidth + 1, cell.Height), Palette.Faint);
    }

    static string Countdown(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "now";
        if (t.TotalMinutes < 60) return $"{Math.Max(1, (int)t.TotalMinutes)}m";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    void PaintHeader(Graphics g, Rectangle r)
    {
        Fill(g, r, Palette.Header);
        int alerts = snapshot.Sessions.Count(IsAlert);
        int busy = snapshot.Sessions.Count(s => s.IsWorking && !IsPaused(s));
        BrandIcons.DrawApp(g, new Rectangle(r.Left + Px(9), r.Top + Px(6), Px(22), Px(22)));
        int x = r.Left + Px(37);
        int titleWidth = Measure("AI Announce", fonts.Title);
        Draw(g, "AI Announce", fonts.Title, new Rectangle(x, r.Top, titleWidth + 2, r.Height), Palette.Text);
        x += titleWidth + Px(8);
        alertPill = Rectangle.Empty;
        if (alerts > 0)
        {
            string count = alerts.ToString();
            alertPill = new Rectangle(x, r.Top + Px(7), Measure(count, fonts.Name) + Px(30), r.Height - Px(14));
            FillRound(g, alertPill, alertPill.Height / 2, Color.FromArgb(pulsing ? 60 + (int)(70 * Pulse()) : 95, Palette.Amber));
            Draw(g, "✋", fonts.Emoji, new Rectangle(x + Px(5), alertPill.Top, Px(17), alertPill.Height), Palette.Amber, CenterText);
            Draw(g, count, fonts.Name, new Rectangle(x + Px(23), alertPill.Top, alertPill.Width - Px(24), alertPill.Height), Palette.AmberText);
            x = alertPill.Right + Px(8);
        }
        menuButton = new Rectangle(r.Right - Px(32), r.Top + Px(5), Px(26), r.Height - Px(10));
        if (hoverMenu) FillRound(g, menuButton, Px(5), Palette.Hover);
        Draw(g, "⋯", fonts.Name, menuButton, Palette.Dim, CenterText);

        bool play = PausedCount > 0;
        bool enabled = play || snapshot.Sessions.Any(s => s.IsWorking) || previewPaused;
        pauseButton = new Rectangle(menuButton.Left - Px(34), r.Top + Px(5), Px(28), r.Height - Px(10));
        FillRound(g, pauseButton, Px(5), hoverPause ? Palette.Hover : Color.FromArgb(45, play ? Palette.Shell : Palette.Accent));
        Color actionColor = pause?.Changing == true ? Palette.Dim : !enabled ? Palette.Faint : play ? Palette.Shell : Palette.Accent;
        using (var brush = new SolidBrush(actionColor))
        {
            if (play) g.FillPolygon(brush, new[] { new Point(pauseButton.Left + Px(10), pauseButton.Top + Px(6)), new Point(pauseButton.Left + Px(10), pauseButton.Bottom - Px(6)), new Point(pauseButton.Right - Px(7), pauseButton.Top + pauseButton.Height / 2) });
            else
            {
                g.FillRectangle(brush, pauseButton.Left + Px(9), pauseButton.Top + Px(6), Px(3), pauseButton.Height - Px(12));
                g.FillRectangle(brush, pauseButton.Left + Px(16), pauseButton.Top + Px(6), Px(3), pauseButton.Height - Px(12));
            }
        }
        int right = pauseButton.Left - Px(8);
        stopButton = Rectangle.Empty;
        int working = snapshot.Sessions.Count(s => s.IsWorking);
        bool stopping = DateTime.UtcNow < stoppingUntil;
        if (working > 0 || stopping)
        {
            bool armed = DateTime.UtcNow < stopArmedUntil;
            string label = stopping ? "Stopping…" : armed ? $"Stop {working}?" : "";
            int width = label.Length == 0 ? Px(22) : Measure(label, fonts.Tag) + Px(26);
            stopButton = new Rectangle(right - width, r.Top + Px(7), width, r.Height - Px(14));
            FillRound(g, stopButton, Px(5), Color.FromArgb(armed || stopping ? 255 : hoverStop ? 170 : 70, Palette.GpuHot));
            Fill(g, new Rectangle(stopButton.Left + Px(7), stopButton.Top + (stopButton.Height - Px(8)) / 2, Px(8), Px(8)), Color.White);
            if (label.Length > 0) Draw(g, label, fonts.Tag, new Rectangle(stopButton.Left + Px(20), stopButton.Top, width - Px(22), stopButton.Height), Color.White);
            right = stopButton.Left - Px(8);
        }
        if (snapshot.Gpus.Count > 0)
        {
            var gpu = snapshot.Gpus[0];
            string text = $"{gpu.Used / 1073741824.0:0.0}/{gpu.Total / 1073741824.0:0} GB";
            int w = Measure(text, fonts.Small);
            if (right - x > w + Px(48))
            {
                Draw(g, text, fonts.Small, new Rectangle(right - w, r.Top, w + 1, r.Height), Palette.Dim);
                right -= w + Px(6);
                if (right - x > Px(65))
                {
                    Bar(g, new Rectangle(right - Px(30), r.Top + r.Height / 2 - Px(3), Px(30), Px(6)), gpu.Total > 0 ? (float)gpu.Used / gpu.Total : 0f);
                    right -= Px(36);
                }
            }
        }
        string counts = play ? $"{PausedCount} paused" : busy > 0 ? $"{busy}/{snapshot.Sessions.Count}" : $"{snapshot.Sessions.Count} sessions";
        if (right - x > Px(24)) Draw(g, counts, fonts.Small, new Rectangle(x, r.Top, right - x, r.Height), play ? Palette.Shell : Palette.Dim);
    }

    void PaintSession(Graphics g, Row row, bool hovered, DateTime now)
    {
        var s = row.Session!;
        var r = row.Bounds;
        bool alert = IsAlert(s);
        if (alert) AlertBand(g, r, s);
        else if (hovered) Fill(g, r, Palette.Hover);

        int x = r.Left + Px(10);
        var icon = new Rectangle(x, r.Top + Px(3), Px(20), Px(20));
        BrandIcons.DrawProvider(g, icon, s.Source);
        Color dot = IsPaused(s) ? Palette.Shell : alert ? Palette.Amber : s.IsWorking ? Palette.Accent : Palette.Faint;
        using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, icon.Right - Px(3), icon.Bottom - Px(3), Px(5), Px(5));
        x += Px(28);

        // ↻ restart button, only for idle sessions on an old build that we can resume in their shell
        int rightEdge = r.Right - Px(12);
        if (ShowRestart(s))
        {
            var button = RestartButton(r);
            bool hot = hoverRestartPid == s.Pid;
            FillRound(g, button, Px(5), Color.FromArgb(hot ? 150 : 55, Palette.BgShell));
            Draw(g, "↻", fonts.Glyph, button, hot ? Color.White : Palette.BgShell, CenterText);
            rightEdge = button.Left - Px(6);
        }

        bool cycling = restarting.Contains(s.Pid);
        string status = IsPaused(s) ? PauseLabel(s) : cycling ? "restarting…" : alert ? (s.State == SessionState.Asked ? "asked" : "needs you") : s.StatusText;
        string right = cycling ? status : $"{status}  {Ago(now - (pause?.Find(s)?.PausedAt ?? s.Since))}";
        int rw = Measure(right, fonts.Small);
        var rr = new Rectangle(rightEdge - rw, r.Top, rw + 1, r.Height);
        Color statusColor = IsPaused(s) ? Palette.Shell : cycling ? Palette.BgShell : alert ? Palette.Amber : s.State == SessionState.Busy ? Palette.Accent : Palette.Faint;
        Draw(g, right, fonts.Small, rr, statusColor);

        // which CLI it is (Claude goes untagged), and "update" when it runs an older build than the one
        // installed, which a restart picks up
        var tags = new List<(string Text, Color Color)>();

        if (s.NeedsRestart) tags.Add(("update", Palette.BgShell));
        int tagsWidth = tags.Sum(t => Measure(t.Text, fonts.Tag) + Px(16));

        int avail = rr.Left - Px(10) - x;
        int nw = Math.Max(0, Math.Min(Measure(s.Title, fonts.Name), avail - tagsWidth));
        Draw(g, s.Title, fonts.Name, new Rectangle(x, r.Top, nw, r.Height),
            s.State == SessionState.Idle && !alert ? Palette.Soft : Palette.Text);
        int cx = x + nw + Px(6);
        foreach (var (text, color) in tags)
        {
            int tagWidth = Measure(text, fonts.Tag) + Px(10);
            if (cx + tagWidth > rr.Left - Px(6)) break;
            var tag = new Rectangle(cx, r.Top + (r.Height - Px(14)) / 2, tagWidth, Px(14));
            FillRound(g, tag, Px(4), Color.FromArgb(46, color));
            Draw(g, text, fonts.Tag, tag, color, CenterText);
            cx = tag.Right + Px(6);
        }
        if (!string.Equals(s.Project, s.Title, StringComparison.OrdinalIgnoreCase) && rr.Left - Px(10) - cx > Px(40))
            Draw(g, "· " + s.Project, fonts.Small, new Rectangle(cx, r.Top, rr.Left - Px(10) - cx, r.Height), Palette.Faint);
    }

    bool ShowRestart(SessionView s) => !IsPaused(s) && s.NeedsRestart && s.CanRestart && !restarting.Contains(s.Pid);

    Rectangle RestartButton(Rectangle row) => new(row.Right - Px(36), row.Top + Px(3), Px(24), row.Height - Px(6));

    void PaintAttention(Graphics g, Row row)
    {
        var s = row.Session!;
        AlertBand(g, row.Bounds, s);
        string text = s.AttentionKind == "asked" ? $"“{s.Attention}”" : s.Attention ?? "";
        Draw(g, text, fonts.Small, Inset(row.Bounds, Px(34), Px(12)), Palette.AmberText);
    }

    void PaintActivity(Graphics g, Row row, DateTime now)
    {
        var s = row.Session!;
        var r = Inset(row.Bounds, Px(34), Px(12));
        string age = Ago(now - s.ActivitySince);
        int aw = Measure(age, fonts.Small);
        Draw(g, age, fonts.Small, new Rectangle(r.Right - aw, r.Top, aw + 1, r.Height), Palette.Faint);
        Draw(g, "↳ " + s.Activity, fonts.Small, new Rectangle(r.Left, r.Top, r.Width - aw - Px(8), r.Height), Palette.Dim);
    }

    void PaintItem(Graphics g, Row row, bool hovered, DateTime now)
    {
        var item = row.Item!;
        if (IsAlert(row.Session!)) AlertBand(g, row.Bounds, row.Session!, band: false);
        if (hovered) Fill(g, row.Bounds, Palette.Hover);
        var r = Inset(row.Bounds, Px(34), Px(12));

        (string tag, Color color) = item.Kind switch
        {
            ItemKind.Agent => ("agent", Palette.Agent),
            ItemKind.BackgroundShell => ("bg shell", Palette.BgShell),
            _ => ("shell", Palette.Shell),
        };
        var pill = new Rectangle(r.Left, r.Top + (r.Height - Px(14)) / 2, Measure(tag, fonts.Tag) + Px(10), Px(14));
        FillRound(g, pill, Px(4), Color.FromArgb(46, color));
        Draw(g, tag, fonts.Tag, pill, color, CenterText);

        string age = Ago(now - item.Started);
        int aw = Measure(age, fonts.Small);
        Draw(g, age, fonts.Small, new Rectangle(r.Right - aw, r.Top, aw + 1, r.Height), Palette.Faint);

        int x = pill.Right + Px(7);
        int avail = r.Right - aw - Px(8) - x;
        bool isCommand = item.Kind != ItemKind.Agent && item.Command != null && item.Label == ToolText.FirstLine(item.Command);
        Font font = isCommand ? fonts.Mono : fonts.Small;
        int lw = Math.Min(Measure(item.Label, font), avail);
        Draw(g, item.Label, font, new Rectangle(x, r.Top, lw, r.Height), Palette.Soft);

        string? extra = item.Kind == ItemKind.Agent ? JoinParts(item.Detail, item.Activity) : null;
        if (extra != null && avail - lw > Px(30))
            Draw(g, "· " + extra, fonts.Small, new Rectangle(x + lw + Px(5), r.Top, avail - lw - Px(5), r.Height), Palette.Faint);
    }

    void AlertBand(Graphics g, Rectangle r, SessionView s, bool band = true)
    {
        int alpha = s.State == SessionState.Waiting ? 20 + (int)(26 * Pulse()) : 24;
        Fill(g, r, Color.FromArgb(alpha, Palette.Amber));
        Fill(g, new Rectangle(r.Left, r.Top, Px(3), r.Height), band ? Palette.Amber : Color.FromArgb(120, Palette.Amber));
    }

    void Bar(Graphics g, Rectangle r, float fraction, Color? color = null)
    {
        FillRound(g, r, r.Height / 2, Color.FromArgb(52, 255, 255, 255));
        fraction = Math.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f) return;
        var fill = new Rectangle(r.Left, r.Top, Math.Max(r.Height, (int)(r.Width * fraction)), r.Height);
        Color c = color ?? (fraction > 0.9f ? Palette.GpuHot : fraction > 0.7f ? Palette.Amber : Palette.GpuOk);
        FillRound(g, fill, r.Height / 2, c);
    }

    // ---- input -------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressed = true;
        dragging = false;
        pressAt = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        lastMouse = e.Location;
        if (pressed && (e.Button & MouseButtons.Left) != 0)
        {
            if (!dragging && (Math.Abs(e.X - pressAt.X) > Px(4) || Math.Abs(e.Y - pressAt.Y) > Px(4)))
            {
                dragging = true;
                tip.Hide(this);
            }
            if (dragging)
            {
                var c = Cursor.Position;
                Location = new Point(c.X - pressAt.X, c.Y - pressAt.Y);
            }
            return;
        }

        int index = HitIndex(e.Location);
        bool overMenu = menuButton.Contains(e.Location);
        bool overStop = stopButton.Contains(e.Location);
        bool overPause = pauseButton.Contains(e.Location);
        int overRestart = index >= 0 && rows[index].Kind == RowKind.Session && rows[index].Session is SessionView rs &&
                          ShowRestart(rs) && RestartButton(rows[index].Bounds).Contains(e.Location)
            ? rs.Pid
            : 0;
        if (index == hover && overMenu == hoverMenu && overStop == hoverStop && overPause == hoverPause && overRestart == hoverRestartPid) return;
        hover = index;
        hoverMenu = overMenu;
        hoverStop = overStop;
        hoverPause = overPause;
        hoverRestartPid = overRestart;
        Cursor = overMenu || overStop || overPause || (index >= 0 && rows[index].Kind is not RowKind.Header and not RowKind.Empty and not RowKind.Footer)
            ? Cursors.Hand
            : Cursors.Default;
        tip.Hide(this);
        hoverDelay.Stop();
        hoverDelay.Start();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        hoverMenu = hoverStop = hoverPause = false;
        hoverRestartPid = 0;
        hoverDelay.Stop();
        tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            int i = HitIndex(e.Location);
            ShowMenu(e.Location, i >= 0 ? rows[i].Session : null);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        bool wasDrag = dragging;
        pressed = false;
        dragging = false;
        if (wasDrag)
        {
            SnapToEdges();
            return;
        }
        if (menuButton.Contains(e.Location))
        {
            ShowMenu(new Point(menuButton.Left, menuButton.Bottom), null);
            return;
        }
        if (pauseButton.Contains(e.Location))
        {
            TogglePause();
            return;
        }
        if (stopButton.Contains(e.Location))
        {
            StopClicked();
            return;
        }
        int index = HitIndex(e.Location);
        if (index < 0) return;
        var row = rows[index];
        if (row.Kind == RowKind.Session && row.Session is SessionView target && ShowRestart(target) &&
            RestartButton(row.Bounds).Contains(e.Location))
        {
            RestartSession(target);
        }
        else if (row.Kind == RowKind.Usage)
        {
            _ = usageMonitor?.RefreshAsync();
        }
        else if (row.Kind == RowKind.Summary)
        {
            settings.HideIdle = false;
            settings.Save();
            LayoutRows();
            Invalidate();
        }
        else if (row.Session != null)
        {
            TerminalFocus.Focus(row.Session.Pid, row.Session.TabTitles);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (pauseButton.Contains(e.Location) || stopButton.Contains(e.Location) || menuButton.Contains(e.Location)) return;
        int i = HitIndex(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0 && rows[i].Kind == RowKind.Header && !menuButton.Contains(e.Location))
        {
            settings.Collapsed = !settings.Collapsed;
            settings.Save();
            LayoutRows();
            Invalidate();
        }
    }

    int HitIndex(Point p) => rows.FindIndex(r => r.Bounds.Contains(p));

    void SnapToEdges()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int margin = Px(8), reach = Px(24);
        int x = Left, y = Top;
        if (Math.Abs(x - wa.Left) < reach) x = wa.Left + margin;
        if (Math.Abs(wa.Right - Right) < reach) x = wa.Right - Width - margin;
        if (Math.Abs(y - wa.Top) < reach) y = wa.Top + margin;
        if (Math.Abs(wa.Bottom - Bottom) < reach) y = wa.Bottom - Height - margin;
        Location = new Point(x, y);
        settings.X = x;
        settings.Y = y;
        settings.Save();
    }

    void ShowTip()
    {
        hoverDelay.Stop();
        if (pressed || hover < 0 || hover >= rows.Count) return;
        string? text = TipFor(rows[hover]);
        if (!string.IsNullOrEmpty(text)) tip.Show(text, this, lastMouse.X + Px(12), lastMouse.Y + Px(18), 15000);
    }

    string? TipFor(Row row)
    {
        if (hoverPause) return PausedCount > 0
            ? $"Play: resume the {PausedCount} session(s) paused by this button.\nPending approvals must be answered in their terminals."
            : "Pause all working sessions and their agents until you click Play.\nSessions that were already idle stay idle.";
        if (hoverStop)
        {
            int working = snapshot.Sessions.Count(s => s.IsWorking);
            return $"Emergency stop: presses Esc in the {working} working session{(working == 1 ? "" : "s")} " +
                   "and stops their background agents.\nClick, then click again within 3 s.";
        }
        if (hoverRestartPid != 0 && row.Session is SessionView target)
            return $"Restart on {target.LatestVersion}: exits Claude and runs claude --resume in the same tab.\n" +
                   "The conversation carries on where it left off.";

        switch (row.Kind)
        {
            case RowKind.Header:
                var lines = snapshot.Gpus.Select(g => $"{g.Name}: {g.Used >> 20:N0} / {g.Total >> 20:N0} MiB VRAM · {g.Utilization}% load").ToList();
                lines.Add("Drag to move · double-click to collapse · right-click for options");
                return string.Join("\n", lines);
            case RowKind.Session or RowKind.Attention or RowKind.Activity:
                var s = row.Session!;
                var info = new List<string> { s.Title, s.Cwd, $"pid {s.Pid} · {s.StatusText} · {s.Product} {s.Version}".TrimEnd() };
                if (IsPaused(s)) info.Add(pause?.Find(s)?.Error ?? "Paused by you. Click Play in the header to resume.");
                if (s.NeedsRestart)
                    info.Add($"{s.LatestVersion} is installed; " +
                             (s.CanRestart ? "↻ restarts it into the new build" : s.IsWorking ? "restart once it's idle" : "restart it from its terminal"));
                if (IsAlert(s) && s.Attention != null) info.Add(s.Attention);
                if (s.Activity != null) info.Add(s.Activity);
                info.Add("Click to jump to its terminal");
                return string.Join("\n", info);
            case RowKind.Item:
                var item = row.Item!;
                var parts = new List<string> { item.Label };
                if (item.Detail != null) parts.Add(item.Kind == ItemKind.Agent ? $"type: {item.Detail}" : $"shell: {item.Detail}");
                if (item.Activity != null) parts.Add($"now: {item.Activity}");
                if (item.Command != null && item.Command != item.Label) parts.Add(ToolText.Clip(item.Command, 600));
                return string.Join("\n", parts);
            case RowKind.Summary:
                return "Click to show idle sessions";
            case RowKind.Notice:
                return row.Text;
            case RowKind.Usage when row.Usage is UsageInfo u:
                DateTime now = DateTime.UtcNow;
                var limits = new List<string> { $"{u.DisplayName} {u.Plan ?? ""} · quota used".Replace("  ", " ") };
                foreach (var l in UsageParsing.Windows(u.Limits, "Not reported."))
                    limits.Add(l.Available ? $"{l.Label}: {l.Percent:0.#}% used" +
                        (l.ResetsAt is DateTime at ? $" · resets {at.ToLocalTime():ddd h:mm tt} ({Countdown(at - now)})" : "")
                        : $"{l.Label}: {l.Note ?? "Not reported"}");
                if (u.Message != null) limits.Add(u.Message);
                if (u.FetchedAt != DateTime.MinValue) limits.Add($"{(u.Live ? "Fetched" : "Reported")} {Ago(now - u.FetchedAt)} ago");
                limits.Add("Click to refresh");
                return string.Join("\n", limits);
            default:
                return null;
        }
    }

    // ---- actions -----------------------------------------------------------------------------

    int PausedCount => pause?.Count ?? (previewPaused ? snapshot.Sessions.Count(s => s.IsWorking) : 0);
    bool IsPaused(SessionView s) => pause?.Find(s) != null || (previewPaused && s.IsWorking);
    string PauseLabel(SessionView s) => previewPaused ? "paused" : pause?.Find(s)?.Error != null ? "pause needs you" : s.IsWorking ? "pausing…" : "paused";

    async void TogglePause()
    {
        if (pause == null || pause.Changing) return;
        try
        {
            if (pause.Count > 0) await pause.ResumeAsync();
            else await pause.PauseAsync(snapshot.Sessions);
        }
        catch (Exception ex) { Log.Write("pause: " + ex.GetType().Name); }
        Invalidate();
    }

    // First click arms the stop for three seconds; the second one sends it.
    void StopClicked()
    {
        if (DateTime.UtcNow >= stopArmedUntil)
        {
            stopArmedUntil = DateTime.UtcNow.AddSeconds(3);
            Invalidate();
            return;
        }
        stopArmedUntil = DateTime.MinValue;
        StopNow(snapshot.Sessions.Where(s => s.IsWorking).ToList());
    }

    void StopNow(IReadOnlyList<SessionView> working)
    {
        if (monitor == null || working.Count == 0) return;
        Log.Write($"emergency stop: {string.Join(", ", working.Select(s => $"{s.Title} ({s.Pid})"))}");
        stoppingUntil = DateTime.UtcNow.AddSeconds(3);
        // the stop retries for up to 10 s; don't announce these as finished turns meanwhile
        foreach (var s in working) quietUntil[s.Pid] = DateTime.UtcNow.AddSeconds(20);
        Invalidate();
        _ = SessionControl.Stop(working, monitor.StatusOf);
    }

    void RestartSession(SessionView s)
    {
        if (monitor == null || IsPaused(s) || !s.CanRestart || !restarting.Add(s.Pid)) return;
        Invalidate();
        SessionControl.Restart(s, monitor.StatusOf).ContinueWith(t => Post(() =>
        {
            // on success the old pid drops out of the next snapshot and its resumed self shows up
            if (!t.Result) restarting.Remove(s.Pid);
            Invalidate();
        }));
    }

    // Staggered so a dozen sessions don't start their MCP servers all at once.
    async void RestartAll(IReadOnlyList<SessionView> sessions)
    {
        foreach (var s in sessions)
        {
            RestartSession(s);
            await System.Threading.Tasks.Task.Delay(1500);
        }
    }

    void AddActionItems(ToolStripItemCollection items)
    {
        items.Add(new ToolStripMenuItem(PausedCount > 0 ? $"Play — resume {PausedCount} paused sessions" : "Pause all working sessions", null, (_, _) => TogglePause())
        { Enabled = pause?.Changing != true && (PausedCount > 0 || snapshot.Sessions.Any(SessionPause.CanPause)) });
        var working = snapshot.Sessions.Where(s => s.IsWorking).ToList();
        if (working.Count > 0)
            items.Add($"Emergency stop ({working.Count} working)", null, (_, _) => StopNow(working));
        var stale = snapshot.Sessions.Where(ShowRestart).ToList();
        if (stale.Count > 0)
            items.Add($"Restart idle sessions on old builds ({stale.Count})", null, (_, _) => RestartAll(stale));
        if (working.Count + stale.Count > 0) items.Add(new ToolStripSeparator());
    }

    // ---- menus -------------------------------------------------------------------------------

    void ShowMenu(Point client, SessionView? s)
    {
        tip.Hide(this);
        var menu = new TopmostMenu();
        if (s != null)
        {
            var go = new ToolStripMenuItem("Go to terminal", null, (_, _) => TerminalFocus.Focus(s.Pid, s.TabTitles));
            go.Font = new Font(go.Font, FontStyle.Bold);
            menu.Items.Add(go);
            menu.Items.Add("Open folder", null, (_, _) => OpenFolder(s.Cwd));
            menu.Items.Add("Copy session ID", null, (_, _) => Clipboard.SetText(s.SessionId));
            if (IsAlert(s))
                menu.Items.Add("Dismiss", null, (_, _) => { dismissed.Add(s.AttentionKey); Apply(snapshot); });
            if (!IsPaused(s) && s.CanRestart && !restarting.Contains(s.Pid))
                menu.Items.Add(s.NeedsRestart ? $"Restart on {s.LatestVersion}" : "Restart session", null, (_, _) => RestartSession(s));
            else if (s.NeedsRestart)
                menu.Items.Add(new ToolStripMenuItem($"Restart on {s.LatestVersion} once it's idle") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
        }
        AddActionItems(menu.Items);
        AddSettingsItems(menu.Items);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Hide overlay", null, (_, _) => SetOverlayVisible(false));
        menu.Items.Add("Exit", null, (_, _) => Close());
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        // the menu only closes on outside clicks if our window is foreground
        Native.SetForegroundWindow(Handle);
        menu.Show(this, client);
    }

    void BuildTrayMenu(ToolStripItemCollection items)
    {
        foreach (var s in snapshot.Sessions.Where(IsAlert))
        {
            var target = s;
            items.Add(new ToolStripMenuItem(ToolText.Clip($"✋ {s.Title}: {s.Attention}", 90), null,
                (_, _) => TerminalFocus.Focus(target.Pid, target.TabTitles)));
        }
        if (items.Count > 0) items.Add(new ToolStripSeparator());
        AddActionItems(items);
        items.Add(Visible ? "Hide overlay" : "Show overlay", null, (_, _) => SetOverlayVisible(!Visible));
        AddSettingsItems(items);
        items.Add(new ToolStripSeparator());
        items.Add("Exit", null, (_, _) => Close());
    }

    void AddSettingsItems(ToolStripItemCollection items)
    {
        items.Add(Toggle("Collapse", settings.Collapsed, () => settings.Collapsed = !settings.Collapsed));
        items.Add(Toggle("Hide idle sessions", settings.HideIdle, () => settings.HideIdle = !settings.HideIdle));
        // Each alert's sound plays with its toast, so it's greyed out while that toast is off. Turning a
        // sound on plays it once, so you know which is which.
        var needsYou = new ToolStripMenuItem("When a session needs you");
        needsYou.DropDownItems.Add(Toggle("Notify", settings.Notifications, () => settings.Notifications = !settings.Notifications));
        needsYou.DropDownItems.Add(Toggle("Play a sound", settings.Sound, () =>
        {
            settings.Sound = !settings.Sound;
            if (settings.Sound) SystemSounds.Exclamation.Play();
        }, enabled: settings.Notifications));
        items.Add(needsYou);
        var finished = new ToolStripMenuItem("When a session finishes");
        finished.DropDownItems.Add(Toggle("Notify", settings.FinishedNotify, () => settings.FinishedNotify = !settings.FinishedNotify));
        finished.DropDownItems.Add(Toggle("Play a sound", settings.FinishedSound, () =>
        {
            settings.FinishedSound = !settings.FinishedSound;
            if (settings.FinishedSound) Chime.Play();
        }, enabled: settings.FinishedNotify));
        items.Add(finished);
        var opacity = new ToolStripMenuItem("Opacity");
        foreach (int pct in new[] { 100, 90, 80, 70 })
            opacity.DropDownItems.Add(Toggle($"{pct}%", Math.Abs(settings.Opacity * 100 - pct) < 1,
                () => { settings.Opacity = pct / 100.0; Opacity = settings.Opacity; }));
        items.Add(opacity);
        items.Add(Toggle("Taskbar button", settings.TaskbarButton, () =>
        {
            settings.TaskbarButton = !settings.TaskbarButton;
            SetTaskbarButton(settings.TaskbarButton);
        }));
        items.Add(Toggle("Start with Windows", AutoStart.Enabled, () => AutoStart.Set(!AutoStart.Enabled)));
        if (usageMonitor != null) items.Add(Toggle("Plan usage", settings.PlanUsage, () => SetPlanUsage(!settings.PlanUsage)));
        if (usageMonitor != null && settings.PlanUsage)
        {
            items.Add("Refresh usage", null, (_, _) => _ = usageMonitor.RefreshAsync());
            items.Add("Usage details...", null, (_, _) =>
            {
                using var details = new UsageDetailsForm(usageMonitor);
                details.ShowDialog(this);
            });
        }
        items.Add("Reset position", null, (_, _) =>
        {
            settings.X = settings.Y = null;
            settings.Save();
            PlaceInitially();
        });
    }

    // Off means no login tokens are read and no quota requests are sent; the strip and the taskbar
    // progress go away with it.
    void SetPlanUsage(bool on)
    {
        settings.PlanUsage = on;
        if (usageMonitor == null) return;
        if (on)
        {
            usageMonitor.Start();
            return;
        }
        usageMonitor.Stop();
        usage = Array.Empty<UsageInfo>();
        UpdateTray();
        UpdateTaskbar();
    }

    ToolStripMenuItem Toggle(string text, bool on, Action flip, bool enabled = true) =>
        new(text, null, (_, _) =>
        {
            flip();
            settings.Save();
            LayoutRows();
            Invalidate();
        }) { Checked = on, Enabled = enabled };

    void SetOverlayVisible(bool visible)
    {
        settings.Hidden = !visible;
        settings.Save();
        if (visible)
        {
            Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Apply(snapshot);
        }
        else
        {
            Hide();
            animation.Enabled = false;
        }
    }

    static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // ---- helpers -----------------------------------------------------------------------------

    int Px(float dip) => (int)Math.Round(dip * scale);

    static double Pulse() => 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 270.0);

    // MeasureText opens a screen DC per call; most strings repeat from frame to frame
    int Measure(string text, Font font)
    {
        if (measured.TryGetValue((text, font), out int width)) return width;
        if (measured.Count > 4000) measured.Clear();
        width = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), LeftText & ~TextFormatFlags.EndEllipsis).Width;
        measured[(text, font)] = width;
        return width;
    }

    // Animation ticks repaint small regions; anything outside them is skipped before it hits GDI.
    void Draw(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags = LeftText)
    {
        if (r.IntersectsWith(paintClip)) TextRenderer.DrawText(g, text, font, r, color, flags);
    }

    void Fill(Graphics g, Rectangle r, Color color)
    {
        if (!r.IntersectsWith(paintClip)) return;
        using var b = new SolidBrush(color);
        g.FillRectangle(b, r);
    }

    void FillRound(Graphics g, Rectangle r, int radius, Color color)
    {
        if (!r.IntersectsWith(paintClip)) return;
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        using var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    static Rectangle Inset(Rectangle r, int left, int right) =>
        new(r.Left + left, r.Top, Math.Max(0, r.Width - left - right), r.Height);

    static string? JoinParts(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }

    static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d";
    }

    static Icon TrayIcon(Color fill, string glyph, string fontName)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font(fontName, 19, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        // two icons for the lifetime of the process, so the HICONs are never freed
        return Icon.FromHandle(bmp.GetHicon());
    }
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Palette.Text : Palette.Faint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Palette.Dim;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, e.ImageRectangle, Palette.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
}

sealed class DarkMenuColors : ProfessionalColorTable
{
    static readonly Color Back = Color.FromArgb(32, 32, 38);
    static readonly Color Selected = Color.FromArgb(54, 54, 64);

    public override Color ToolStripDropDownBackground => Back;
    public override Color ImageMarginGradientBegin => Back;
    public override Color ImageMarginGradientMiddle => Back;
    public override Color ImageMarginGradientEnd => Back;
    public override Color MenuBorder => Palette.Border;
    public override Color MenuItemBorder => Selected;
    public override Color MenuItemSelected => Selected;
    public override Color MenuItemSelectedGradientBegin => Selected;
    public override Color MenuItemSelectedGradientEnd => Selected;
    public override Color MenuItemPressedGradientBegin => Selected;
    public override Color MenuItemPressedGradientEnd => Selected;
    public override Color SeparatorDark => Palette.Border;
    public override Color SeparatorLight => Back;
    public override Color CheckBackground => Back;
    public override Color CheckSelectedBackground => Selected;
    public override Color CheckPressedBackground => Selected;
}
