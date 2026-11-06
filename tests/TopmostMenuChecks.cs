using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AIAnnounce;

static class TopmostMenuChecks
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    sealed class TestWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOPMOST; return cp; }
        }
        public TestWindow()
        {
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(20, 20, 100, 100);
            Text = "AI Announce menu test";
        }
    }

    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var overlay = new TestWindow();
                using var competitor = new OtherApplication();
                using var menu = new TopmostMenu();
                var opacity = new ToolStripMenuItem("Opacity");
                opacity.DropDownItems.Add("100%");
                menu.Items.Add(opacity);
                overlay.Show();
                menu.Show(overlay, new Point(5, 5));
                opacity.ShowDropDown();
                Application.DoEvents();
                check(menu.Visible && opacity.DropDown.Visible, "Menu and submenu open normally");
                check((GetWindowLongPtr(menu.Handle, -20).ToInt64() & Native.WS_EX_TOPMOST) != 0,
                    "Menu has native topmost style");
                check((GetWindowLongPtr(opacity.DropDown.Handle, -20).ToInt64() & Native.WS_EX_TOPMOST) != 0,
                    "Nested submenu has native topmost style");

                // Reproduce a native owner/style update that demotes a popup; the
                // competing application then covers it until our timer recovers.
                Native.SetWindowPos(opacity.DropDown.Handle, new IntPtr(-2), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Native.SetWindowPos(menu.Handle, new IntPtr(-2), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                competitor.Raise();
                check(Above(competitor.Handle, menu.Handle), "Test recreates another topmost window covering the menu");
                IntPtr focused = Native.GetForegroundWindow();
                PumpUntil(() => Above(menu.Handle, competitor.Handle), TimeSpan.FromSeconds(2));
                check(menu.Visible && Above(menu.Handle, competitor.Handle), "Open menu timer restores it above another topmost window");
                check(Above(opacity.DropDown.Handle, menu.Handle), "Submenu stays above its parent after recovery");
                check(Native.GetForegroundWindow() == focused, "Periodic menu raise does not change foreground focus");

                Raise(overlay.Handle);
                TopmostMenu.RaiseOpenMenus();
                check(Above(menu.Handle, overlay.Handle) && Above(opacity.DropDown.Handle, menu.Handle),
                    "Overlay housekeeping preserves the open menu stack");
                menu.Close();
                TopmostMenu.RaiseOpenMenus();
                check(!menu.Visible && !opacity.DropDown.Visible, "Closing the menu also closes its submenus");
                menu.Show(overlay, new Point(5, 5));
                opacity.ShowDropDown();
                competitor.Raise();
                PumpUntil(() => Above(menu.Handle, competitor.Handle), TimeSpan.FromSeconds(2));
                check(menu.Visible && Above(opacity.DropDown.Handle, menu.Handle), "Reopened tray-style menu still handles submenus");
                menu.Close();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new Exception("Menu checks timed out");
        if (failure != null) throw new Exception("Menu checks failed", failure);
    }

    // Use another process: same-thread WinForms popups share an owner and do not
    // reproduce the ordering of a competing application's topmost window.
    sealed class OtherApplication : IDisposable
    {
        readonly Process process;
        public IntPtr Handle { get; }
        public OtherApplication()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--menu-test-window");
            process = Process.Start(start)!;
            Handle = new IntPtr(long.Parse(ReadLine()));
        }
        string ReadLine() => process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
            ?? throw new Exception("Topmost test helper exited");
        public void Raise()
        {
            process.StandardInput.WriteLine("raise");
            process.StandardInput.Flush();
            if (ReadLine() != "raised") throw new Exception("Topmost helper failed to raise its window");
        }
        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("exit");
                process.StandardInput.Flush();
                if (!process.WaitForExit(3000)) process.Kill();
            }
            process.Dispose();
        }
    }

    public static void RunOtherApplication()
    {
        using var form = new TestWindow();
        form.Shown += (_, _) =>
        {
            Console.WriteLine(form.Handle.ToInt64());
            _ = Task.Run(() =>
            {
                while (Console.ReadLine() is string command)
                {
                    if (command == "exit") { form.BeginInvoke(new Action(form.Close)); break; }
                    if (command == "raise") form.BeginInvoke(new Action(() =>
                    {
                        bool raised = Native.SetWindowPos(form.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                        Console.WriteLine(raised ? "raised" : "failed");
                    }));
                }
            });
        };
        Application.Run(form);
    }

    static void Raise(IntPtr hwnd) => Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    static bool Above(IntPtr upper, IntPtr lower)
    {
        for (IntPtr next = Native.GetWindow(upper, 2); next != IntPtr.Zero; next = Native.GetWindow(next, 2))
            if (next == lower) return true;
        return false;
    }

    static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < timeout) { Application.DoEvents(); Thread.Sleep(10); }
    }
}
