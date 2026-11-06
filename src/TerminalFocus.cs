using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AIAnnounce;

// Brings a session's terminal to the front. Windows Terminal hosts many sessions as tabs of one
// window, so after finding the window we select the tab whose title matches the session
// (Claude titles its tab "<spinner glyph> <name or AI title>").
static class TerminalFocus
{
    const string TerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    const string IslandClass = "Windows.UI.Composition.DesktopWindowContentBridge";

    public static void Focus(int pid, IReadOnlyList<string> titles)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { FocusCore(pid, titles); }
            catch (Exception ex) { Log.Write($"focus {pid}: {ex.Message}"); }
        });
    }

    // The terminal window title is the active tab's title.
    public static bool IsForeground(IReadOnlyList<string> titles)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || Native.ClassName(fg) != TerminalClass) return false;
        string shown = Native.WindowText(fg);
        return titles.Any(t => Matches(shown, t, exact: true) || Matches(shown, t, exact: false));
    }

    // Exact after dropping a leading glyph. Loose: Codex puts a spinner or "[ . ] Action Required |" in
    // front of its "<thread> | <project>" title and changes it from second to second, so the tail is
    // what stays put.
    static bool Matches(string tab, string title, bool exact)
    {
        string shown = StripGlyph(tab), want = StripGlyph(title);
        if (string.Equals(shown, want, StringComparison.OrdinalIgnoreCase)) return true;
        return !exact && want.Contains(" | ", StringComparison.Ordinal) && shown.EndsWith(" " + want, StringComparison.OrdinalIgnoreCase);
    }

    static void FocusCore(int pid, IReadOnlyList<string> titles)
    {
        IntPtr window = ConsoleHost(pid);
        bool selected = false;
        if (titles.Count > 0 && (window == IntPtr.Zero || Native.ClassName(window) == TerminalClass))
        {
            var candidates = window != IntPtr.Zero ? new List<IntPtr> { window } : TerminalWindows();
            foreach (IntPtr w in candidates)
            {
                if (!SelectTab(w, titles)) continue;
                window = w;
                selected = true;
                break;
            }
        }
        if (!selected && window != IntPtr.Zero && Native.ClassName(window) == TerminalClass)
            selected = SelectByMarker(window, pid);
        if (window == IntPtr.Zero) window = AncestorWindow(pid);
        if (window == IntPtr.Zero)
        {
            Log.Write($"focus {pid}: no window found");
            return;
        }
        BringToFront(window);
        Log.Write($"focus {pid}: hwnd {window:X}, tab {(selected ? "selected" : "not matched")}");
    }

    // AttachConsole reaches the console of that exact process. Under ConPTY the console window is
    // a hidden stub owned by the terminal window that actually shows the session.
    static IntPtr ConsoleHost(int pid)
    {
        lock (ConsoleInput.Gate)  // one console per process; typing into sessions uses it too
        {
            Native.FreeConsole();
            if (!Native.AttachConsole((uint)pid)) return IntPtr.Zero;
            IntPtr console = Native.GetConsoleWindow();
            Native.FreeConsole();
            if (console == IntPtr.Zero) return IntPtr.Zero;
            // The ConPTY stub ("PseudoConsoleWindow") even reports itself visible, so always follow
            // the owner chain; a classic conhost window has no owner and is returned as is.
            IntPtr owner = Native.GetAncestor(console, Native.GA_ROOTOWNER);
            return owner != IntPtr.Zero ? owner : console;
        }
    }

    static List<IntPtr> TerminalWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.ClassName(h) == TerminalClass) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr AncestorWindow(int pid)
    {
        var procs = Processes.Snapshot();
        var visited = new HashSet<int>();
        for (int current = pid; procs.TryGetValue(current, out var p) && visited.Add(current); current = p.ParentPid)
        {
            IntPtr found = IntPtr.Zero;
            int owner = current;
            Native.EnumWindows((h, _) =>
            {
                Native.GetWindowThreadProcessId(h, out uint windowPid);
                if (windowPid != owner || !Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero || Native.WindowText(h).Length == 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    // Tabs live in the XAML island; its automation tree hangs off the island's child window,
    // not the top-level terminal window.
    static List<(IUIAutomationElement Element, string Name)> Tabs(IUIAutomation uia, IntPtr window)
    {
        var hosts = new List<IntPtr>();
        Native.EnumChildWindows(window, (h, _) =>
        {
            if (Native.ClassName(h) == IslandClass) hosts.Add(h);
            return true;
        }, IntPtr.Zero);
        hosts.Add(window);

        var condition = uia.CreatePropertyCondition(Uia.ControlTypePropertyId, Uia.TabItemControlTypeId);
        var tabs = new List<(IUIAutomationElement, string)>();
        foreach (IntPtr host in hosts)
        {
            var found = uia.ElementFromHandle(host).FindAll(Uia.TreeScopeDescendants, condition);
            for (int i = 0, n = found.GetLength(); i < n; i++)
            {
                var element = found.GetElement(i);
                tabs.Add((element, element.GetCurrentName() ?? ""));
            }
            if (tabs.Count > 0) break;
        }
        return tabs;
    }

    static bool SelectTab(IntPtr window, IReadOnlyList<string> titles)
    {
        var tabs = Tabs(Uia.Create(), window);
        // titles are in priority order (user name, custom title, AI title, derived name); every exact
        // match beats any loose one
        foreach (bool exact in new[] { true, false })
            foreach (string title in titles)
                foreach (var (element, name) in tabs)
                {
                    if (!Matches(name, title, exact)) continue;
                    if (element.GetCurrentPattern(Uia.SelectionItemPatternId) is IUIAutomationSelectionItemPattern item) item.Select();
                    return true;
                }
        return false;
    }

    // No tab carries a title we know: agy leaves its shell's title in place, and "Windows PowerShell"
    // could be any tab. Retitle the session's console for a moment, pick the tab that shows the new
    // title, then put the old one back. A renamed tab ignores the console title; then this times out.
    static bool SelectByMarker(IntPtr window, int pid)
    {
        string marker = $"AIAnnounce {pid}:{Environment.TickCount64 % 100000}";
        string? original = ConsoleInput.SwapTitle(pid, marker);
        if (original == null) return false;
        try
        {
            long deadline = Environment.TickCount64 + 600;
            do
            {
                if (SelectTab(window, new[] { marker })) return true;
                Thread.Sleep(40);
            } while (Environment.TickCount64 < deadline);
            return false;
        }
        finally
        {
            ConsoleInput.SwapTitle(pid, original);
        }
    }

    // A shell's own title ("Windows PowerShell", a path, "cmd.exe") says nothing about the session in it
    // and would match every other tab running that shell.
    public static bool IsShellTitle(string title) =>
        title.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase) ||
        title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        (title.Length > 2 && title[1] == ':' && title[2] == '\\');

    // --tabs
    public static IEnumerable<string> Describe()
    {
        var uia = Uia.Create();
        foreach (IntPtr w in TerminalWindows())
        {
            yield return $"window {w:X} \"{Native.WindowText(w)}\"";
            foreach (var (_, name) in Tabs(uia, w))
                yield return $"  tab \"{name}\"";
        }
    }

    // --focus <pid>: same path as a click, but synchronous
    public static void FocusNow(int pid, IReadOnlyList<string> titles) => FocusCore(pid, titles);

    // "✳ my-project" / "◐ my-project" -> "my-project"
    static string StripGlyph(string? s)
    {
        s = (s ?? "").Trim();
        int space = s.IndexOf(' ');
        if (space > 0 && space <= 2 && !s[..space].Any(char.IsLetterOrDigit)) s = s[(space + 1)..];
        return s.Trim();
    }

    static void BringToFront(IntPtr window)
    {
        if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(window) && Native.GetForegroundWindow() == window) return;

        // Foreground lock: borrow the input queue of whoever has focus, then retry.
        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        Native.BringWindowToTop(window);
        Native.SetForegroundWindow(window);
        if (attached) Native.AttachThreadInput(me, fgThread, false);
    }
}
