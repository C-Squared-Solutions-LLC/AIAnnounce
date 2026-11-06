using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AIAnnounce;

// One row of ~/.claude/sessions/<pid>.json. Claude Code keeps this file current for every running
// process: status is busy | idle | waiting, and waitingFor says what a waiting session wants.
sealed record RegistryEntry(
    int Pid, string SessionId, string Cwd, long ProcStart, DateTime StartedAt, string? Name, string? NameSource,
    string Status, string? WaitingFor, DateTime StatusSince, string? Entrypoint, string? Version);

sealed class SessionMonitor : IDisposable
{
    readonly string sessionsDir;
    readonly string projectsDir;
    readonly Dictionary<string, TranscriptTracker> trackers = new();
    readonly Dictionary<(int Pid, long Created), ShellProc?> shellCache = new();
    readonly GpuMonitor gpu = new();
    readonly AutoResetEvent wake = new(false);
    readonly FileSystemWatcher? watcher;
    Dictionary<int, List<ProcEntry>> children = new();
    readonly Dictionary<int, (int Pid, string Exe)> shellOf = new();
    readonly Dictionary<(int Pid, long Start), string?> launchCommands = new();
    readonly CodexMonitor codex = new();
    readonly AgyMonitor agy = new();
    readonly Dictionary<string, (string Message, DateTime At)> lastFailure = new();
    DateTime nextProcessScan, nextVersionCheck;
    Version? latestInstalled;
    readonly string claudeDir;
    Thread? thread;
    volatile bool stopping;
    volatile Snapshot latest = Snapshot.Empty;
    public Snapshot Latest => latest;

    public SessionMonitor(string claudeDir)
    {
        this.claudeDir = claudeDir;
        sessionsDir = Path.Combine(claudeDir, "sessions");
        projectsDir = Path.Combine(claudeDir, "projects");
        try
        {
            if (Directory.Exists(sessionsDir))
            {
                // status flips land here first; react immediately instead of waiting for the next poll
                watcher = new FileSystemWatcher(sessionsDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                watcher.Changed += (_, _) => wake.Set();
                watcher.Created += (_, _) => wake.Set();
                watcher.Deleted += (_, _) => wake.Set();
                watcher.Renamed += (_, _) => wake.Set();
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Write("watcher: " + ex.Message);
        }
    }

    public event Action<Snapshot>? Updated;

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "SessionMonitor" };
        thread.Start();
    }

    void Loop()
    {
        bool first = true;
        while (!stopping)
        {
            try { Updated?.Invoke(Build()); }
            catch (Exception ex) { Log.Write("build: " + ex); }
            if (first)
            {
                // the first pass parses several MB of every transcript; hand that memory back once
                first = false;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            if (wake.WaitOne(1000)) Thread.Sleep(60);  // let the writer finish, coalesce bursts
        }
    }

    public Snapshot Build()
    {
        DateTime now = DateTime.UtcNow;
        var live = ReadRegistry()
            .Where(e => Processes.IsAlive(e.Pid, e.ProcStart))
            .GroupBy(e => e.SessionId)
            .Select(g => g.OrderByDescending(e => e.StatusSince).First())
            .OrderBy(e => e.StartedAt)
            .ToList();

        // A full process snapshot walks every thread on the box (~30 ms with a dozen sessions and
        // their MCP servers), so new shells are discovered on a slower beat. Exited shells still
        // drop out on the next poll because ToolShell re-checks each one.
        if (now >= nextProcessScan)
        {
            var procs = Processes.Snapshot();
            children = new Dictionary<int, List<ProcEntry>>();
            foreach (var p in procs.Values)
            {
                if (!children.TryGetValue(p.ParentPid, out var list)) children[p.ParentPid] = list = new List<ProcEntry>();
                list.Add(p);
            }
            foreach (var key in shellCache.Keys.Where(k => !procs.ContainsKey(k.Pid)).ToList()) shellCache.Remove(key);
            shellOf.Clear();
            foreach (var e in live)
                if (procs.TryGetValue(e.Pid, out var self) && procs.TryGetValue(self.ParentPid, out var parent))
                    shellOf[e.Pid] = (parent.Pid, parent.Exe);
            // Codex and agy have no session registry: their processes are the list of live sessions
            codex.Scan(procs);
            agy.Scan(procs);
            bool busy = live.Any(e => e.Status != "idle") || codex.AnyBusy || agy.AnyBusy;
            nextProcessScan = now.AddSeconds(busy ? 2 : 6);
        }
        if (now >= nextVersionCheck)
        {
            latestInstalled = SessionControl.LatestInstalled(claudeDir);
            nextVersionCheck = now.AddMinutes(1);
        }

        var views = new List<SessionView>();
        var current = new HashSet<string>();
        foreach (var entry in live)
        {
            current.Add(entry.SessionId);
            if (!trackers.TryGetValue(entry.SessionId, out var tracker))
                trackers[entry.SessionId] = tracker = new TranscriptTracker(projectsDir, entry.SessionId, entry.Cwd);
            tracker.Update();

            var shells = new List<ShellProc>();
            if (children.TryGetValue(entry.Pid, out var kids))
                foreach (var kid in kids)
                    if (ToolShell(kid) is ShellProc shell) shells.Add(shell);

            var key = (entry.Pid, entry.ProcStart);
            if (!launchCommands.TryGetValue(key, out string? launch))
                launchCommands[key] = launch = Processes.CommandLine(entry.Pid);
            shellOf.TryGetValue(entry.Pid, out var parentShell);
            bool outdated = latestInstalled != null && Version.TryParse(entry.Version, out var running) && running < latestInstalled;
            views.Add(Compose(entry, tracker, shells, now, launch, parentShell, outdated ? latestInstalled!.ToString() : null));
        }

        foreach (string gone in trackers.Keys.Where(k => !current.Contains(k)).ToList()) trackers.Remove(gone);
        var liveKeys = live.Select(e => (e.Pid, e.ProcStart)).ToHashSet();
        foreach (var gone in launchCommands.Keys.Where(k => !liveKeys.Contains(k)).ToList()) launchCommands.Remove(gone);

        // a broken Codex or agy read mustn't cost the Claude rows their update
        Guarded("codex", () => views.AddRange(codex.Build(now)));
        Guarded("agy", () => views.AddRange(agy.Build(now)));
        return latest = new Snapshot(views, gpu.Sample(), now);
    }

    void Guarded(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // once per distinct failure every 10 minutes, not once a second
            string message = ex.GetType().Name + ": " + ex.Message;
            if (lastFailure.TryGetValue(what, out var last) && last.Message == message && DateTime.UtcNow - last.At < TimeSpan.FromMinutes(10)) return;
            lastFailure[what] = (message, DateTime.UtcNow);
            Log.Write($"{what}: {ex}");
        }
    }

    List<RegistryEntry> ReadRegistry()
    {
        var entries = new List<RegistryEntry>();
        if (!Directory.Exists(sessionsDir)) return entries;
        foreach (string file in Directory.EnumerateFiles(sessionsDir, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out _)) continue;
            try
            {
                using var doc = JsonDocument.Parse(Json.ReadShared(file));
                var r = doc.RootElement;
                int pid = (int)Json.Long(r, "pid");
                string? sessionId = Json.Str(r, "sessionId");
                if (pid <= 0 || sessionId == null) continue;
                long statusAt = Json.Long(r, "statusUpdatedAt");
                entries.Add(new RegistryEntry(
                    pid, sessionId, Json.Str(r, "cwd") ?? "", Json.Long(r, "procStart"),
                    Json.FromUnixMs(Json.Long(r, "startedAt")), Json.Str(r, "name"), Json.Str(r, "nameSource"),
                    Json.Str(r, "status") ?? "unknown", Json.Str(r, "waitingFor"),
                    Json.FromUnixMs(statusAt > 0 ? statusAt : Json.Long(r, "updatedAt")),
                    Json.Str(r, "entrypoint"), Json.Str(r, "version")));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // mid-write or deleted underneath us; the next poll picks it up
            }
        }
        return entries;
    }

    ShellProc? ToolShell(ProcEntry p)
    {
        if (p.Exe.ToLowerInvariant() is not ("bash.exe" or "pwsh.exe" or "powershell.exe")) return null;
        long created = Processes.CreationTime(p.Pid);
        if (created == 0) return null;
        var key = (p.Pid, created);
        if (!shellCache.TryGetValue(key, out var shell))
            shellCache[key] = shell = Processes.ToolShell(p, created);
        return shell;
    }

    static SessionView Compose(RegistryEntry e, TranscriptTracker t, List<ShellProc> shells, DateTime now,
        string? launchCommand, (int Pid, string? Exe) parentShell, string? latestVersion)
    {
        DateTime processStart = e.ProcStart > 0 ? DateTime.FromFileTimeUtc(e.ProcStart) : e.StartedAt;
        if (e.Status == "idle") t.DropPendingBefore(e.StatusSince);

        var items = new List<ItemView>();
        var agents = t.RunningAgents(processStart, now);
        foreach (var a in agents)
            items.Add(new ItemView { Kind = ItemKind.Agent, Label = a.Label, Detail = a.Type, Activity = a.Activity, Started = a.Started });

        var claimed = new HashSet<string>();
        foreach (var shell in shells.OrderBy(s => s.Started))
        {
            if (now - shell.Started < TimeSpan.FromSeconds(1.5)) continue;  // skip blink-and-gone commands
            var task = t.Tasks.Values.FirstOrDefault(x => !x.IsAgent && !claimed.Contains(x.Id) && ToolText.SameCommand(x.Command, shell.Command));
            var call = task == null
                ? t.Pending.Values.FirstOrDefault(c => c.Command != null && !claimed.Contains(c.Id) && ToolText.SameCommand(c.Command, shell.Command))
                : null;
            if (task != null) claimed.Add(task.Id);
            if (call != null) claimed.Add(call.Id);
            string label = task?.Label ?? call?.Description ?? ToolText.FirstLine(shell.Command)
                           ?? (shell.Shell == "pwsh" ? "PowerShell script" : "shell");
            items.Add(new ItemView
            {
                Kind = task != null ? ItemKind.BackgroundShell : ItemKind.Shell,
                Label = label,
                Detail = shell.Shell,
                Command = task?.Command ?? call?.Command ?? shell.Command,
                Started = shell.Started,
            });
        }

        SessionState state;
        string statusText;
        string? attention = null, attentionKind = null;
        switch (e.Status)
        {
            case "waiting":
                state = SessionState.Waiting;
                statusText = "needs you";
                (attentionKind, attention) = DescribeWait(e.WaitingFor, t, agents);
                break;
            case "idle":
                string? question = t.EndedOnText ? ToolText.TrailingQuestion(t.LastText) : null;
                if (question != null)
                {
                    state = SessionState.Asked;
                    statusText = "asked";
                    attention = question;
                    attentionKind = "asked";
                }
                else
                {
                    state = SessionState.Idle;
                    statusText = "idle";
                }
                break;
            case "busy":
                state = SessionState.Busy;
                statusText = "working";
                break;
            default:
                // older builds report e.g. "shell" while a ! command runs
                state = SessionState.Busy;
                statusText = e.Status;
                break;
        }

        string? activity = null;
        DateTime activitySince = default;
        if (state == SessionState.Busy)
        {
            var call = t.Pending.Values
                .Where(c => !ToolText.IsShellOrAgent(c.Name) && now - c.Started > TimeSpan.FromSeconds(2))
                .OrderByDescending(c => c.Started)
                .FirstOrDefault();
            if (call != null)
            {
                activity = ToolText.Describe(call);
                activitySince = call.Started;
            }
        }

        // Same precedence Claude uses for the terminal tab: a name the user gave wins, otherwise the AI title.
        string project = ProjectName(e.Cwd);
        string? userName = e.NameSource is null or "user" or "peer" ? e.Name : null;
        var titles = new[] { userName, t.CustomTitle, t.AiTitle, e.Name }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SessionView
        {
            SessionId = e.SessionId,
            Pid = e.Pid,
            Title = titles.Count > 0 ? titles[0] : project,
            TabTitles = titles,
            Project = project,
            Cwd = e.Cwd,
            State = state,
            StatusText = statusText,
            Since = e.StatusSince,
            Attention = attention,
            AttentionKind = attentionKind,
            Activity = activity,
            ActivitySince = activitySince,
            Items = items,
            Entrypoint = e.Entrypoint,
            Version = e.Version,
            TranscriptPath = t.TranscriptPath,
            ProcStart = e.ProcStart,
            LaunchCommand = launchCommand,
            ShellPid = parentShell.Pid,
            ShellExe = parentShell.Exe,
            LatestVersion = latestVersion,
        };
    }

    // Straight from the registry, for the last check before typing into a session.
    public string? StatusOf(int pid)
    {
        var other = Latest.Sessions.FirstOrDefault(s => s.Pid == pid && s.Source != Cli.Claude);
        if (other != null) return other.State switch { SessionState.Busy => "busy", SessionState.Waiting => "waiting", _ => "idle" };
        try
        {
            using var doc = JsonDocument.Parse(Json.ReadShared(Path.Combine(sessionsDir, pid + ".json")));
            return Json.Str(doc.RootElement, "status");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static (string Kind, string Text) DescribeWait(string? waitingFor, TranscriptTracker t, List<AgentRun> agents)
    {
        var pending = t.Pending.Values.OrderByDescending(c => c.Started).ToList();
        if (pending.FirstOrDefault(c => c.Name == "AskUserQuestion") is ToolCall ask)
            return ("question", ask.Question ?? "Claude has a question");
        if (pending.Any(c => c.Name == "ExitPlanMode"))
            return ("plan", "Plan ready for your approval");

        switch (waitingFor)
        {
            case null:
            case "permission prompt":
                if (pending.FirstOrDefault(c => c.Name is not ("Agent" or "Task")) is ToolCall call)
                    return ("permission", $"Allow {ToolText.Describe(call)}?");
                // permission prompts raised inside a subagent only show up in that agent's log
                if (agents.FirstOrDefault(a => a.Activity != null) is AgentRun agent)
                    return ("permission", $"Agent \"{agent.Label}\" wants {agent.Activity}");
                return ("permission", "Permission needed");
            case "input needed": return ("input", "An MCP server needs your input");
            case "sandbox request": return ("permission", "Sandboxed command wants network access");
            case "worker request": return ("permission", "A worker needs approval");
            case "goal proposal": return ("question", "Claude proposed a session goal");
            case "dialog open": return ("dialog", "Dialog open in the terminal");
            case "": return ("other", "Waiting for you");
            default: return ("other", char.ToUpperInvariant(waitingFor[0]) + waitingFor[1..]);
        }
    }

    public static string ProjectName(string cwd)
    {
        string trimmed = cwd.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        watcher?.Dispose();
        thread?.Join(1500);
    }
}
