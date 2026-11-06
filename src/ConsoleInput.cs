using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace AIAnnounce;

// Types into another process's console by writing key events straight into its input buffer:
// the same keys you'd press in that terminal, without focusing or even seeing the window. A
// process can be attached to only one console at a time, so everything here takes Gate.
static class ConsoleInput
{
    public static readonly object Gate = new();

    const ushort KEY_EVENT = 0x0001;
    const uint LEFT_CTRL_PRESSED = 0x0008, SHIFT_PRESSED = 0x0010;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    public readonly record struct Stroke(ushort Vk, char Ch, uint Modifiers, int PauseMs = 0);

    public static readonly Stroke Escape = new(0x1B, '\x1b', 0);
    public static readonly Stroke Enter = new(0x0D, '\r', 0);

    public static Stroke Ctrl(char letter)
    {
        char upper = char.ToUpperInvariant(letter);
        return new Stroke(upper, (char)(upper - '@'), LEFT_CTRL_PRESSED);
    }

    public static Stroke Pause(int ms) => new(0, '\0', 0, ms);

    public static IEnumerable<Stroke> Text(string text)
    {
        foreach (char ch in text)
        {
            short scan = VkKeyScanW(ch);
            yield return scan == -1
                ? new Stroke(0xE7 /* VK_PACKET */, ch, 0)
                : new Stroke((ushort)(scan & 0xFF), ch, (scan & 0x100) != 0 ? SHIFT_PRESSED : 0);
        }
    }

    // Never send a control action to a recycled PID or a terminal that switched conversations.
    public static bool SendSession(SessionView expected, Func<SessionView?> current, IEnumerable<Stroke> strokes)
    {
        lock (Gate)
        {
            if (!Processes.IsAlive(expected.Pid, expected.ProcStart) || current() is not SessionView live || !SessionPause.Same(expected, live)) return false;
            var keys = strokes.ToList();
            if (keys.Contains(Enter) && live.State is not (SessionState.Idle or SessionState.Asked)) return false;
            if (keys.Contains(Ctrl('x')) && (live.Source != Cli.Claude || !live.Items.Any(i => i.Kind == ItemKind.Agent))) return false;
            return Send(expected.Pid, keys);
        }
    }

    // Attach to the console `pid` lives in, write the strokes (honouring pauses), detach.
    public static bool Send(int pid, IEnumerable<Stroke> strokes)
    {
        lock (Gate)
        {
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)pid)) return false;
            IntPtr input = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            try
            {
                if (input == IntPtr.Zero || input == new IntPtr(-1)) return false;
                var batch = new List<INPUT_RECORD>();
                foreach (var stroke in strokes)
                {
                    if (stroke.PauseMs > 0)
                    {
                        if (!Flush(input, batch)) return false;
                        Thread.Sleep(stroke.PauseMs);
                        continue;
                    }
                    batch.Add(Record(stroke, down: true));
                    batch.Add(Record(stroke, down: false));
                }
                return Flush(input, batch);
            }
            finally
            {
                if (input != IntPtr.Zero && input != new IntPtr(-1)) Native.CloseHandle(input);
                Native.FreeConsole();
            }
        }
    }

    // What that console currently shows (the visible window of its screen buffer). Read-only.
    public static string? ReadScreen(int pid)
    {
        lock (Gate)
        {
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)pid)) return null;
            IntPtr output = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            try
            {
                if (output == IntPtr.Zero || output == new IntPtr(-1) || !GetConsoleScreenBufferInfo(output, out var info)) return null;
                var lines = new List<string>();
                int width = info.Window.Right - info.Window.Left + 1;
                var buffer = new char[width];
                for (short y = info.Window.Top; y <= info.Window.Bottom; y++)
                {
                    ReadConsoleOutputCharacterW(output, buffer, (uint)width, new COORD { X = info.Window.Left, Y = y }, out uint read);
                    lines.Add(new string(buffer, 0, (int)read).TrimEnd());
                }
                return string.Join("\n", lines).TrimEnd();
            }
            finally
            {
                if (output != IntPtr.Zero && output != new IntPtr(-1)) Native.CloseHandle(output);
                Native.FreeConsole();
            }
        }
    }

    // The console's title, which is what its Windows Terminal tab shows unless the tab was renamed.
    public static string? ReadTitle(int pid) => WithConsole(pid, CurrentTitle);

    // Retitles the console and hands back the title it replaced; null if the console is out of reach.
    public static string? SwapTitle(int pid, string title) =>
        WithConsole(pid, () => CurrentTitle() is var old && SetConsoleTitleW(title) ? old : null);

    static string? WithConsole(int pid, Func<string?> action)
    {
        lock (Gate)
        {
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)pid)) return null;
            try { return action(); }
            finally { Native.FreeConsole(); }
        }
    }

    static string CurrentTitle()
    {
        var buffer = new char[1024];
        uint length = GetConsoleTitleW(buffer, (uint)buffer.Length);
        return new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).TrimEnd('\0');
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetConsoleTitleW([Out] char[] title, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetConsoleTitleW(string title);

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct SMALL_RECT { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD Size;
        public COORD CursorPosition;
        public ushort Attributes;
        public SMALL_RECT Window;
        public COORD MaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleScreenBufferInfo(IntPtr output, out CONSOLE_SCREEN_BUFFER_INFO info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ReadConsoleOutputCharacterW(IntPtr output, [Out] char[] buffer, uint length, COORD start, out uint read);

    static bool Flush(IntPtr input, List<INPUT_RECORD> batch)
    {
        if (batch.Count == 0) return true;
        var records = batch.ToArray();
        batch.Clear();
        return WriteConsoleInputW(input, records, (uint)records.Length, out uint written) && written == records.Length;
    }

    static INPUT_RECORD Record(Stroke s, bool down) => new()
    {
        EventType = KEY_EVENT,
        Key = new KEY_EVENT_RECORD
        {
            KeyDown = down ? 1 : 0,
            RepeatCount = 1,
            VirtualKeyCode = s.Vk,
            VirtualScanCode = (ushort)MapVirtualKeyW(s.Vk, 0),
            UnicodeChar = s.Ch,
            ControlKeyState = s.Modifiers,
        },
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct KEY_EVENT_RECORD
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD Key;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleInputW(IntPtr input, INPUT_RECORD[] records, uint length, out uint written);

    [DllImport("user32.dll")]
    static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKeyW(uint code, uint mapType);
}
