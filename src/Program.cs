using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AIAnnounce;

static class Program
{
    const string InstanceMutex = @"Local\AIAnnounce.Overlay";
    const string ShowEvent = @"Local\AIAnnounce.Show";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--agy-statusline")) return AgyUsageFeed.Receive();
        string claudeDir = ArgValue(args, "--claude-dir")
                           ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                           ?? Path.Combine(UserPaths.Home, ".claude");
        if (args.Contains(ClaudeUsageFeed.Flag)) return ClaudeUsageFeed.Receive(new ClaudeUsageFeed(claudeDir));
        // The installer runs this just before it deletes the exe: undo what outside the app still points at it.
        if (args.Contains("--uninstall") && Environment.ProcessPath is string self)
        {
            AutoStart.RemoveFor(self);
            try { new AgyUsageFeed().Disconnect(self); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Log.Write("agy status line cleanup: " + ex.Message);
            }
            new ClaudeUsageFeed(claudeDir).Disconnect(self);
            return 0;
        }
        if (ArgValue(args, "--write-icon") is string iconPath)
        {
            using var output = File.Create(iconPath);
            BrandIcons.WriteIcon(output);
            return 0;
        }
        // the diagnostics below print to the shell that started them
        if (args.Any(a => a is "--dump" or "--bench" or "--tabs" or "--type" or "--screen" or "--stop" or "--restart" or "--focus"))
            ConsoleOut.Use();

        if (args.Contains("--dump")) return Dump(claudeDir);
        if (args.Contains("--usage") || args.Contains("--usage-report"))
        {
            using var usage = new UsageMonitor(claudeDir);
            var results = usage.RefreshAsync().GetAwaiter().GetResult();
            UsageDiagnostics.Save(results, command: true);
            string report = UsageDiagnostics.Format(results);
            if (ArgValue(args, "--usage-report") is string reportPath)
            {
                File.WriteAllText(reportPath, report);
                UsageDiagnostics.WriteConsole(report);
            }
            else if (!UsageDiagnostics.WriteConsole(report))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new UsageDetailsForm(usage));
            }
            return 0;
        }
        if (args.Contains("--bench"))
        {
            using var monitor = new SessionMonitor(claudeDir);
            monitor.Build();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) monitor.Build();
            Console.WriteLine($"steady-state Build(): {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");
            sw.Restart();
            for (int i = 0; i < 20; i++) Processes.Snapshot();
            Console.WriteLine($"process snapshot: {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");
            var gpu = new GpuMonitor();
            sw.Restart();
            for (int i = 0; i < 20; i++) gpu.Sample();
            Console.WriteLine($"gpu sample: {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");
            return 0;
        }
        if (args.Contains("--tabs")) return Tabs();
        // --type <pid> "text{enter}{esc}{ctrl+d}{pause:300}": raw key injection, for checking the plumbing
        if (ArgValue(args, "--type") is string typePid && int.TryParse(typePid, out int typeTarget) && args.Length > Array.IndexOf(args, "--type") + 2)
        {
            string spec = args[Array.IndexOf(args, "--type") + 2];
            var strokes = new List<ConsoleInput.Stroke>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(spec, @"\{([^}]+)\}|[^{]+"))
            {
                string token = m.Groups[1].Value;
                if (!m.Groups[1].Success) strokes.AddRange(ConsoleInput.Text(m.Value));
                else if (token == "enter") strokes.Add(ConsoleInput.Enter);
                else if (token == "esc") strokes.Add(ConsoleInput.Escape);
                else if (token.StartsWith("ctrl+")) strokes.Add(ConsoleInput.Ctrl(token[5]));
                else if (token.StartsWith("pause:")) strokes.Add(ConsoleInput.Pause(int.Parse(token[6..])));
            }
            Console.WriteLine(ConsoleInput.Send(typeTarget, strokes) ? "sent" : "failed");
            return 0;
        }

        if (ArgValue(args, "--screen") is string screenPid && int.TryParse(screenPid, out int screenTarget))
        {
            string? screen = ConsoleInput.ReadScreen(screenTarget);
            Console.WriteLine(screen ?? "(couldn't attach)");
            Console.WriteLine($"-- title: {ConsoleInput.ReadTitle(screenTarget) ?? "?"}");
            if (screen != null && CodexMonitor.ApprovalOnScreen(screen) is string approval)
                Console.WriteLine($"-- reads as a Codex approval prompt: {approval}");
            return 0;
        }

        // --stop <pid> / --restart <pid>: what the overlay's buttons do, for one session
        foreach (string verb in new[] { "--stop", "--restart" })
        {
            if (ArgValue(args, verb) is not string target || !int.TryParse(target, out int targetPid)) continue;
            using var monitor = new SessionMonitor(claudeDir);
            var session = monitor.Build().Sessions.FirstOrDefault(s => s.Pid == targetPid);
            if (session == null)
            {
                Console.WriteLine("no such session");
                return 1;
            }
            if (verb == "--stop") SessionControl.Stop(new[] { session }, monitor.StatusOf).Wait();
            else Console.WriteLine(SessionControl.Restart(session, monitor.StatusOf).Result ? "restarted" : "not restarted (see log)");
            return 0;
        }

        if (ArgValue(args, "--focus") is string focusPid && int.TryParse(focusPid, out int pid))
        {
            using var monitor = new SessionMonitor(claudeDir);
            var session = monitor.Build().Sessions.FirstOrDefault(s => s.Pid == pid);
            Console.WriteLine(session == null ? "no such session" : $"titles: {string.Join(" | ", session.TabTitles)}");
            if (session != null) TerminalFocus.FocusNow(pid, session.TabTitles);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // an overlay that lives all day should log a bad frame, not stop on a WinForms error dialog
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("ui: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        ToolStripManager.Renderer = new DarkMenuRenderer();

        if (ArgValue(args, "--render") is string png)
        {
            Snapshot snap;
            IReadOnlyList<UsageInfo> usage;
            if (args.Contains("--demo"))
            {
                snap = Demo();
                usage = new[] { new UsageInfo(new[]
                {
                    new UsageLimit("Session", 46, DateTime.UtcNow.AddHours(2.8), "normal"),
                    new UsageLimit("Week", 73, DateTime.UtcNow.AddDays(3.2), "warning"),
                    new UsageLimit("Fable wk", 31, DateTime.UtcNow.AddDays(3.2), "normal"),
                }, "Team", DateTime.UtcNow, true),
                new UsageInfo(new[] { new UsageLimit("Session", 28, DateTime.UtcNow.AddHours(3), ""), new UsageLimit("Week", 61, DateTime.UtcNow.AddDays(4), "") }, "Plus", DateTime.UtcNow, true, Cli.Codex),
                new UsageInfo(new[] { new UsageLimit("Session", 42, DateTime.UtcNow.AddHours(1), ""), new UsageLimit("Week", 34, DateTime.UtcNow.AddDays(2), "") }, "Pro", DateTime.UtcNow, true, Cli.Agy, "agy"),
                };
            }
            else
            {
                using var monitor = new SessionMonitor(claudeDir);
                snap = monitor.Build();
                using var usageMonitor = new UsageMonitor(claudeDir);
                usage = usageMonitor.RefreshAsync().GetAwaiter().GetResult();
            }
            using var preview = new OverlayForm(claudeDir, null, snap, usage, args.Contains("--paused"), ArgValue(args, "--limit-hit"));
            preview.RenderTo(png);
            return 0;
        }

        using var mutex = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            // already running: ask that instance to show its overlay and leave
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEvent);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        // focusing a session briefly attaches to its console; don't die from that console's Ctrl+C
        Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
        Application.Run(new OverlayForm(claudeDir, show));
        return 0;
    }

    static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int Dump(string claudeDir)
    {
        using var monitor = new SessionMonitor(claudeDir);
        var snap = monitor.Build();
        Console.WriteLine($"{snap.Sessions.Count} live session(s) under {claudeDir}");
        foreach (var s in snap.Sessions)
        {
            string build = s.Source == Cli.Claude ? $", v{s.Version}" : "";
            Console.WriteLine($"- [{s.State}] {s.Title} ({s.Project}) {s.Product} pid {s.Pid}, {s.StatusText} for {(snap.At - s.Since).TotalMinutes:0.0}m{build}");
            Console.WriteLine($"    tab titles: {string.Join(" ‖ ", s.TabTitles)}");
            if (s.Source == Cli.Claude)
            {
                Console.WriteLine($"    version {s.Version}{(s.NeedsRestart ? $" -> {s.LatestVersion} installed, needs restart" : " (current)")}; " +
                                  $"shell {s.ShellExe ?? "?"} {s.ShellPid}; can restart now: {s.CanRestart}");
                Console.WriteLine($"    resume: {SessionControl.ResumeCommand(s) ?? "(not resumable from here)"}");
            }
            Console.WriteLine($"    {(s.Source == Cli.Agy ? "conversation" : "transcript")}: {s.TranscriptPath ?? "(not found yet)"}");
            if (s.Attention != null) Console.WriteLine($"    attention [{s.AttentionKind}]: {s.Attention}");
            if (s.Activity != null) Console.WriteLine($"    activity: {s.Activity}");
            foreach (var i in s.Items)
                Console.WriteLine($"    {i.Kind}: {i.Label} | {i.Detail} | {i.Activity} | {(snap.At - i.Started).TotalSeconds:0}s | {ToolText.FirstLine(i.Command)}");
        }
        foreach (var g in snap.Gpus)
            Console.WriteLine($"GPU {g.Name}: {g.Used >> 20} / {g.Total >> 20} MiB, {g.Utilization}% load");
        return 0;
    }

    static int Tabs()
    {
        // same apartment the focus code runs in (thread pool = MTA)
        var worker = new Thread(() =>
        {
            foreach (string line in TerminalFocus.Describe())
                Console.WriteLine(line);
        });
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();
        return 0;
    }

    // Every state at once, for checking the visuals with --render out.png --demo
    static Snapshot Demo()
    {
        DateTime now = DateTime.UtcNow;
        var sessions = new List<SessionView>
        {
            new()
            {
                SessionId = "a", Pid = 58208, Title = "API gateway", Project = "api-gateway",
                Cwd = @"C:\src\api-gateway", State = SessionState.Waiting, StatusText = "needs you",
                Version = "2.1.312", LatestVersion = "2.1.316", ShellPid = 1, ShellExe = "pwsh.exe",
                Since = now.AddSeconds(-42), Attention = "Allow Bash: git push origin feature/rate-limits?", AttentionKind = "permission",
                Items =
                {
                    new ItemView { Kind = ItemKind.Agent, Label = "Retry and backoff fixes", Detail = "backend-architect", Activity = "Edit: retry_policy.go", Started = now.AddMinutes(-14) },
                    new ItemView { Kind = ItemKind.BackgroundShell, Label = "Watch the soak test log", Command = "until grep -qE \"PASS|FAIL\" soak.log; do sleep 10; done", Started = now.AddMinutes(-31) },
                },
            },
            new()
            {
                SessionId = "b", Pid = 60560, Title = "Docs site", Project = "docs-site",
                Cwd = @"C:\src\docs-site", State = SessionState.Busy, StatusText = "working",
                Since = now.AddMinutes(-3), Activity = "github/search_issues: broken links", ActivitySince = now.AddSeconds(-9),
                Items =
                {
                    new ItemView { Kind = ItemKind.Shell, Label = "npm run build", Command = "npm run build", Detail = "bash", Started = now.AddSeconds(-48) },
                    new ItemView { Kind = ItemKind.Agent, Label = "Audit image alt text", Detail = "general-purpose", Activity = "WebFetch: developer.mozilla.org/docs", Started = now.AddMinutes(-2) },
                },
            },
            new()
            {
                SessionId = "g", Pid = 87244, Source = Cli.Codex, Title = "Fix flaky checkout test", Project = "storefront",
                Cwd = @"C:\src\storefront", State = SessionState.Waiting, StatusText = "needs you",
                Since = now.AddSeconds(-25), Attention = "Allow command: pytest tests/test_checkout.py -x --count 20?", AttentionKind = "permission",
                Items =
                {
                    new ItemView { Kind = ItemKind.Shell, Label = "pytest tests/test_checkout.py -x --count 20", Started = now.AddSeconds(-25) },
                },
            },
            new()
            {
                SessionId = "h", Pid = 89292, Source = Cli.Agy, Title = "Landing page hero art", Project = "marketing-site",
                Cwd = @"C:\src\marketing-site", State = SessionState.Busy, StatusText = "working", Since = now.AddMinutes(-3),
                Items =
                {
                    new ItemView { Kind = ItemKind.Agent, Label = "Hero illustration artist", Detail = "image-generator", Activity = "generate_image", Started = now.AddSeconds(-52) },
                },
            },
            new()
            {
                SessionId = "c", Pid = 60524, Title = "Mobile app", Project = "mobile-app",
                Cwd = @"C:\src\mobile-app", State = SessionState.Asked, StatusText = "asked",
                Since = now.AddMinutes(-5), Attention = "Want me to also add the dark-mode variants?", AttentionKind = "asked",
            },
            new()
            {
                SessionId = "d", Pid = 3684, Title = "Design tokens", Project = "mobile-app",
                Cwd = @"C:\src\mobile-app", State = SessionState.Waiting, StatusText = "needs you",
                Since = now.AddSeconds(-8), Attention = "Which spacing scale should the new components use?", AttentionKind = "question",
            },
            new()
            {
                SessionId = "e", Pid = 57264, Title = "Billing service", Project = "billing-service",
                Cwd = @"C:\src\billing-service", State = SessionState.Idle, StatusText = "idle",
                Version = "2.1.312", LatestVersion = "2.1.316", ShellPid = 1, ShellExe = "pwsh.exe",
                Since = now.AddHours(-2).AddMinutes(-14),
            },
            new()
            {
                SessionId = "f", Pid = 55812, Title = "Infra", Project = "infra",
                Cwd = @"C:\src\infra", State = SessionState.Idle, StatusText = "idle",
                Version = "2.1.312", LatestVersion = "2.1.316", ShellPid = 1, ShellExe = "pwsh.exe",
                Since = now.AddMinutes(-37),
            },
        };
        return new Snapshot(sessions, new[] { new GpuSample("RTX 4080", 4357UL << 20, 16376UL << 20, 15) }, now);
    }
}
