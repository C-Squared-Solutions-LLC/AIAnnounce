using System.Text.Json;
using AIAnnounce;

static class Tests
{
    static int checks;
    static void Check(bool ok, string message)
    {
        checks++;
        if (!ok) throw new Exception(message);
    }
    static UsageInfo? Codex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return CodexUsage.Parse(doc.RootElement, DateTime.UtcNow, false);
    }
    static IReadOnlyList<UsageInfo> Agy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AgyUsage.Parse(doc.RootElement, DateTime.UtcNow);
    }
    static SessionView Session(int pid, Cli provider, SessionState state, long start = 123, string? id = null, bool agent = false) => new()
    {
        Pid = pid, Source = provider, State = state, ProcStart = start, SessionId = id ?? $"s{pid}",
        Title = $"Test {pid}", Cwd = "C:\\test", Project = "test", Since = DateTime.UtcNow,
        Items = agent ? new() { new ItemView { Kind = ItemKind.Agent, Label = "agent" } } : new(),
    };

    [STAThread]
    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--menu-test-window")) { TopmostMenuChecks.RunOtherApplication(); return 0; }
        if (args.Length == 3 && args[0] == "--feed-test") return AgyUsageFeed.Receive(new AgyUsageFeed(args[1], args[2]));
        try { await Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task Run()
    {
        var weekly = Codex("""{"limit_id":"codex","plan_type":"prolite","primary":{"used_percent":2,"window_minutes":10080,"resets_at":1794242939},"secondary":null}""")!;
        Check(!weekly.Limits[0].Available && weekly.Limits[1].Percent == 2, "Weekly-only Codex must not become a session limit");
        var both = Codex("""{"primary":{"used_percent":24.5,"window_minutes":300,"resets_at":1794242939},"secondary":{"used_percent":72,"window_minutes":10080,"resets_at":1794242939}}""")!;
        Check(both.Limits[0].Percent == 24.5 && both.Limits[1].Warning, "Codex rollout windows and warning");
        Check(both.Limits[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1794242939).UtcDateTime, "Codex Unix seconds");
        var api = Codex("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":8,"limit_window_seconds":18000,"reset_at":1794242939},"secondary_window":{"used_percent":40,"limit_window_seconds":604800}}}""")!;
        Check(api.Limits[0].Percent == 8 && api.Limits[1].Percent == 40, "Codex account endpoint schema");
        var rpc = Codex("""{"limitId":"codex","primary":{"usedPercent":50,"windowDurationMins":10080}}""")!;
        Check(!rpc.Limits[0].Available && rpc.Limits[1].Percent == 50, "Codex camelCase weekly schema");
        Check(Codex("""{"primary":{"used_percent":9}}""") == null, "Unknown window length must not be guessed");
        Check(Codex("""{"limit_id":"review","primary":{"used_percent":99,"window_minutes":300}}""") == null, "Other Codex buckets must not replace overall quota");
        Check(Codex("null") == null, "Null quota is not zero usage");
        Check(!new UsageLimit("Session", 99, null, "critical", false).Critical, "Missing limit must not trigger a critical taskbar");
        var quota = Agy("""{"buckets":[{"bucketId":"gemini-5-hour","remainingFraction":0.75,"resetTime":"2026-11-03T12:00:00Z"},{"bucketId":"gemini-weekly","remainingFraction":0.4}],"groups":[{"buckets":[{"bucketId":"gemini-weekly","remainingFraction":0.4},{"bucketId":"claude-gpt-weekly","remainingFraction":0.15}]}]}""");
        Check(quota.Count == 1 && quota[0].Limits.Count == 2, "Only Gemini quotas are shown; duplicated buckets and Claude/GPT quotas are excluded");
        Check(Math.Abs(quota[0].Limits[0].Percent - 25) < .001 && Math.Abs(quota[0].Limits[1].Percent - 60) < .001, "agy remaining fractions convert to usage");
        var statusLine = Agy("""{"quota":{"gemini-weekly":{"remaining_fraction":0,"reset_time":"2026-11-03T12:00:00Z"},"claude-5h":{"disabled":true,"remaining_fraction":1}}}""");
        Check(statusLine[0].Limits[1].Percent == 100 && statusLine[0].Limits[1].Critical, "agy exhausted quota remains 100 percent");
        Check(statusLine.Count == 1 && !statusLine[0].Limits[0].Available, "A missing Gemini session window never falls back to Claude's quota");
        Check(!Agy("""{"quota":{"gemini-5h":{"disabled":true,"remaining_fraction":1}}}""")[0].Limits[0].Available, "Disabled Gemini bucket is unavailable");
        Check(Agy("""{"buckets":[{"bucketId":"gemini-weekly","remainingAmount":400}]}""")[0].Limits.All(l => !l.Available), "Amounts without denominators are not percentages");
        Check(Agy("""{"quota":{"non-gemini-weekly":{"remaining_fraction":0},"non_gemini-5h":{"remaining_fraction":0},"nonGemini-weekly":{"remaining_fraction":0},"claude-weekly":{"remaining_fraction":0},"gpt-weekly":{"remaining_fraction":0},"all-models-weekly":{"remaining_fraction":0}}}""").Count == 0, "Non-Gemini and shared quotas cannot become Gemini usage");
        var geminiOnly = Agy("""{"quota":{"non-gemini-weekly":{"remaining_fraction":0},"GEMINI-weekly":{"remaining_fraction":0.9},"claude-5h":{"remaining_fraction":0}}}""");
        Check(geminiOnly.Count == 1 && Math.Abs(geminiOnly[0].Limits[1].Percent - 10) < .001 && !geminiOnly[0].Limits[1].Critical,
            "Exhausted other-model quotas must not raise Gemini's usage or taskbar severity");
        var grouped = Agy("""{"groups":[{"displayName":"Non Gemini","buckets":[{"bucketId":"session","remainingFraction":0}]},{"displayName":"Gemini models","buckets":[{"bucketId":"session","remainingFraction":0.7},{"bucketId":"weekly","remainingFraction":0.2},{"bucketId":"non-gemini-weekly","remainingFraction":0}]}]}""");
        Check(grouped.Count == 1 && Math.Abs(grouped[0].Limits[0].Percent - 30) < .001 && Math.Abs(grouped[0].Limits[1].Percent - 80) < .001,
            "Generic window IDs inherit the Gemini group; explicit other-model buckets stay excluded");
        Check(Agy("""{"buckets":[{"bucketId":"weekly","remainingFraction":0}]}""").Count == 0,
            "Unidentified account-wide quota must not substitute for Gemini quota");

        string dir = Path.Combine(Path.GetTempPath(), "AIAnnounce-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var live = new Dictionary<int, SessionView>
        {
            [1] = Session(1, Cli.Claude, SessionState.Busy),
            [2] = Session(2, Cli.Codex, SessionState.Busy),
            [3] = Session(3, Cli.Agy, SessionState.Busy),
            [4] = Session(4, Cli.Claude, SessionState.Idle),
            [5] = Session(5, Cli.Agy, SessionState.Waiting),
        };
        var sent = new List<(int Pid, List<ConsoleInput.Stroke> Keys)>();
        var fail = new HashSet<int>();
        bool temporaryReadFailure = false;
        SessionView? Current(SessionView s) => temporaryReadFailure ? null : live.GetValueOrDefault(s.Pid);
        Task<bool> Send(SessionView s, IEnumerable<ConsoleInput.Stroke> keys)
        {
            if (fail.Contains(s.Pid)) return Task.FromResult(false);
            sent.Add((s.Pid, keys.ToList()));
            return Task.FromResult(true);
        }
        bool Alive(SessionView s) => live.TryGetValue(s.Pid, out var view) && view.ProcStart == s.ProcStart;
        string path = Path.Combine(dir, "pause.json");
        var pause = new SessionPause(Current, Send, path, Alive);
        await pause.PauseAsync(live.Values.ToList());
        Check(pause.Count == 3 && sent.Count == 3 && sent.All(s => s.Keys.SequenceEqual(new[] { ConsoleInput.Escape })), "Pause interrupts every active provider, not idle/approval sessions");
        Check(File.Exists(path), "Pause survives overlay restart");
        var restored = new SessionPause(Current, Send, path, Alive);
        Check(restored.Count == 3, "Restore the exact paused set");
        foreach (int pid in new[] { 1, 2, 3 }) live[pid] = Session(pid, live[pid].Source, SessionState.Idle);
        foreach (var e in pause.Entries) e.LastSent = DateTime.UtcNow.AddMinutes(-1);
        await pause.MaintainAsync();
        Check(pause.Entries.All(e => e.Notified), "Each interrupted session receives the pause instruction");
        Check(sent.Skip(3).All(s => new string(s.Keys.Where(k => (k.Modifiers & 8) == 0 && !char.IsControl(k.Ch)).Select(k => k.Ch).ToArray()).Contains("Announce")), "Pause instruction is typed");
        int afterNotice = sent.Count;
        foreach (var e in pause.Entries) e.LastSent = DateTime.UtcNow.AddMinutes(-1);
        await pause.MaintainAsync();
        Check(sent.Count == afterNotice, "Idle paused sessions are not repeatedly prompted");
        live[2] = Session(2, Cli.Codex, SessionState.Busy);
        await pause.MaintainAsync();
        Check(sent.Count == afterNotice + 1 && sent.Last().Keys.SequenceEqual(new[] { ConsoleInput.Escape }), "Reawakened paused session is interrupted");
        live[2] = Session(2, Cli.Codex, SessionState.Waiting);
        fail.Add(3);
        await pause.ResumeAsync();
        Check(pause.Count == 2 && pause.Entries.All(e => e.Session.Pid is 2 or 3), "Play removes only successfully resumed sessions");
        Check(pause.Entries.All(e => e.Error != null), "Pending approval and send failure stay visible");
        Check(sent.All(s => s.Pid is 1 or 2 or 3), "Play never wakes an originally idle session");
        temporaryReadFailure = true;
        await pause.MaintainAsync();
        Check(pause.Count == 2, "Transient monitor failure retains paused set");
        temporaryReadFailure = false;
        live[2] = Session(2, Cli.Codex, SessionState.Idle, id: "codex-2");
        await pause.MaintainAsync();
        Check(pause.Count == 2, "Startup placeholder identity retains paused set until the conversation is resolved");
        fail.Clear();
        live[2] = Session(2, Cli.Codex, SessionState.Idle);
        await pause.ResumeAsync();
        Check(pause.Count == 0 && new SessionPause(Current, Send, path, Alive).Count == 0, "Successful Play clears persistent set");
        live[1] = Session(1, Cli.Claude, SessionState.Busy);
        live[2] = Session(2, Cli.Codex, SessionState.Busy);
        live[3] = Session(3, Cli.Agy, SessionState.Busy);
        await pause.PauseAsync(live.Values.ToList());
        int beforeReplacement = sent.Count;
        live[1] = Session(1, Cli.Claude, SessionState.Idle, start: 999);
        live[2] = Session(2, Cli.Codex, SessionState.Idle, id: "different-conversation");
        live.Remove(3);
        await pause.ResumeAsync();
        Check(sent.Count == beforeReplacement && pause.Count == 0, "PID reuse, conversation switch and exit never receive resume input");
        fail.Add(1);
        live[1] = Session(1, Cli.Claude, SessionState.Busy);
        await pause.PauseAsync(live.Values.ToList());
        Check(pause.Count == 0 && pause.Error != null, "Failed interrupt is never reported as paused");
        Check(!SessionPause.Same(Session(1, Cli.Claude, SessionState.Idle, 0), Session(1, Cli.Claude, SessionState.Idle, 0)), "Unknown process creation time is not a safe identity");
        Check(!SessionPause.CanPause(Session(8, Cli.Codex, SessionState.Busy, id: "codex-8")), "Unresolved conversations are not pause targets");
        foreach (Cli provider in Enum.GetValues<Cli>())
        {
            var keys = SessionPause.Prompt(provider, SessionPause.ResumePrompt).ToList();
            Check(keys.Last() == ConsoleInput.Enter && keys[^2].PauseMs > 0, $"{provider} submit has a paste settling delay");
            Check(provider == Cli.Claude || keys.All(k => k != ConsoleInput.Ctrl('c') && k != ConsoleInput.Ctrl('k')), $"{provider} does not receive exit/fast approval chords");
        }

        // A slow sender must serialize Pause, maintenance, and Play.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var slow = new SessionPause(Current, async (s, keys) => { started.SetResult(); await release.Task; return true; }, Path.Combine(dir, "slow.json"), Alive);
        var work = slow.PauseAsync(new[] { live[1] });
        await started.Task;
        await slow.ResumeAsync();
        Check(slow.Changing && !work.IsCompleted, "Play cannot race an in-flight pause");
        release.SetResult();
        await work;
        Check(slow.Count == 1 && !slow.Changing, "Serialized pause finishes correctly");

        fail.Clear();
        live[1] = Session(1, Cli.Claude, SessionState.Busy, agent: true);
        var agents = new SessionPause(Current, Send, Path.Combine(dir, "agents.json"), Alive);
        await agents.PauseAsync(new[] { live[1] });
        live[1] = Session(1, Cli.Claude, SessionState.Idle, agent: true);
        agents.Entries[0].LastSent = DateTime.UtcNow.AddMinutes(-1);
        await agents.MaintainAsync();
        Check(agents.Entries[0].AgentsStopped && sent.Last().Keys.Count(k => k == ConsoleInput.Ctrl('k')) == 2, "Claude's active agents get the confirmed stop chord");
        live[1] = Session(1, Cli.Claude, SessionState.Idle);
        agents.Entries[0].LastSent = DateTime.UtcNow.AddMinutes(-1);
        await agents.MaintainAsync();
        Check(agents.Entries[0].Notified && !sent.Last().Keys.Contains(ConsoleInput.Ctrl('k')), "No stop-agents chord is sent after agents are gone");
        await UsageChecks.Run(Check);
        await ClaudeUsageChecks.Run(Check);
        await AgyFeedChecks.Run(Check);
        TopmostMenuChecks.Run(Check);
        Console.WriteLine($"PASS: {checks} checks (quota schemas, credentials, diagnostics, menu z-order, pause/resume, persistence, identity and races)");
    }
}
