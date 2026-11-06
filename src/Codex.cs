using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIAnnounce;

sealed class CodexCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTime Started { get; init; }
    public string? Command { get; init; }  // the shell command the call runs, when it runs one
    public string Summary { get; init; } = "";
}

// One thread's rollout, followed from its first line: turn boundaries, open tool calls, a question put
// to the user, and the reply the last turn ended on. Each line's two "type" fields are read off its first
// bytes and only the few kinds that matter get parsed; reasoning, token counts and item echoes are most
// of the file.
sealed class CodexRollout
{
    static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 256 };
    // code-mode cells run commands as tools.exec_command({cmd: "..."}), quoted any way JS allows
    static readonly Regex CmdRx = new(@"\bcmd\s*:\s*(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)'|`((?:[^`\\]|\\.)*)`)", RegexOptions.Compiled);

    readonly LineTail tail;
    string? questionCall;  // a blocking request_user_input; the async one returns at once

    public CodexRollout(string path) => tail = new LineTail(path);

    public string Path => tail.Path;
    public string? Cwd { get; private set; }
    public bool TurnOpen { get; private set; }
    public DateTime TurnStarted { get; private set; }
    public DateTime TurnEnded { get; private set; }
    public DateTime LastLine { get; private set; }
    public string? LastReply { get; private set; }
    public string? Question { get; private set; }
    public DateTime QuestionAt { get; private set; }
    public Dictionary<string, CodexCall> Pending { get; } = new();

    public bool Update() => tail.Read(OnLine);

    void OnLine(ReadOnlySpan<byte> line)
    {
        var head = line[..Math.Min(line.Length, 600)];
        var outer = TypeAfter(head, 0, out int next);
        if (next < 0) return;
        var inner = TypeAfter(head, next, out _);
        DateTime at = Stamp(head);
        if (at > LastLine) LastLine = at;

        if (outer.SequenceEqual("session_meta"u8))
        {
            if (Cwd == null) OnMeta(line);
        }
        else if (outer.SequenceEqual("event_msg"u8))
        {
            if (inner.SequenceEqual("task_started"u8))
            {
                TurnOpen = true;
                TurnStarted = at;
                Pending.Clear();
            }
            else if (inner.SequenceEqual("task_complete"u8))
            {
                EndTurn(at);
                using var doc = Parse(line);
                LastReply = doc == null ? null : Json.Str(Json.Get(doc.RootElement, "payload"), "last_agent_message");
            }
            else if (inner.SequenceEqual("turn_aborted"u8))
            {
                EndTurn(at);
                LastReply = null;
            }
        }
        else if (outer.SequenceEqual("response_item"u8))
        {
            if (inner.SequenceEqual("function_call"u8) || inner.SequenceEqual("custom_tool_call"u8))
                OnCall(line, at);
            else if (inner.SequenceEqual("function_call_output"u8) || inner.SequenceEqual("custom_tool_call_output"u8))
            {
                if (CallId(head) is not string id) return;
                Pending.Remove(id);
                if (id == questionCall)
                {
                    questionCall = null;
                    Question = null;
                }
            }
            else if (inner.SequenceEqual("message"u8) && head.IndexOf("\"role\":\"user\""u8) >= 0)
                OnUserMessage(line);
        }
    }

    void EndTurn(DateTime at)
    {
        TurnOpen = false;
        TurnEnded = at;
        Pending.Clear();
    }

    void OnMeta(ReadOnlySpan<byte> line)
    {
        using var doc = Parse(line);
        if (doc != null) Cwd = Json.Str(Json.Get(doc.RootElement, "payload"), "cwd") ?? "";
    }

    void OnCall(ReadOnlySpan<byte> line, DateTime at)
    {
        using var doc = Parse(line);
        if (doc == null) return;
        var p = Json.Get(doc.RootElement, "payload");
        string? id = Json.Str(p, "call_id"), name = Json.Str(p, "name");
        if (id == null || name == null) return;
        string? input = Json.Str(p, "input");          // custom tools take raw text: a code-mode cell
        string? arguments = Json.Str(p, "arguments");  // function tools take JSON

        if (name is "request_user_input_async" or "request_user_input")
        {
            // The async form returns {"accepted":true} straight away and the turn carries on; the
            // answer comes back later as a user message.
            Question = QuestionIn(arguments) ?? "Codex has a question";
            QuestionAt = at;
            questionCall = name == "request_user_input" ? id : null;
            if (questionCall == null) return;
        }

        string? command = name switch
        {
            "exec" => FirstCmd(input),
            "exec_command" or "shell" or "shell_command" or "local_shell" => ShellCommand(arguments),
            _ => null,
        };
        Pending[id] = new CodexCall { Id = id, Name = name, Started = at, Command = command, Summary = Summarize(name, input, arguments) };
    }

    void OnUserMessage(ReadOnlySpan<byte> line)
    {
        using var doc = Parse(line);
        var content = doc == null ? default : Json.Get(Json.Get(doc.RootElement, "payload"), "content");
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            string text = (Json.Str(block, "text") ?? "").TrimStart();
            // environment and instruction blocks ride along as user messages; a reply to a question
            // and a typed prompt don't start with a tag
            if (text.Length == 0 || (text[0] == '<' && !text.StartsWith("<send_user_message_question_reply>", StringComparison.Ordinal)))
                continue;
            Question = null;
            questionCall = null;
            LastReply = null;
            return;
        }
    }

    static string Summarize(string name, string? input, string? arguments)
    {
        if (input != null) return ToolText.Clip(ToolText.FirstLine(input) ?? "", 160);
        if (arguments == null) return "";
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return name == "sleep" && Json.Long(doc.RootElement, "duration_ms") is > 0 and var ms
                ? $"{ms / 1000} s"
                : ToolText.Summarize(name, doc.RootElement);
        }
        catch (JsonException) { return ""; }
    }

    static string? QuestionIn(string? arguments)
    {
        if (arguments == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return ToolText.QuestionIn(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    static string? FirstCmd(string? js)
    {
        if (js == null || CmdRx.Match(js) is not { Success: true } m) return null;
        string raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i + 1 == raw.Length)
            {
                sb.Append(raw[i]);
                continue;
            }
            char e = raw[++i];
            sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
        }
        return sb.ToString();
    }

    // {"cmd": "..."} or {"command": ["pwsh", "-Command", "..."]}
    static string? ShellCommand(string? arguments)
    {
        if (arguments == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var r = doc.RootElement;
            if ((Json.Str(r, "cmd") ?? Json.Str(r, "command")) is string s) return s;
            var argv = Json.Get(r, "command");
            if (argv.ValueKind == JsonValueKind.Array && argv.GetArrayLength() > 0)
                return argv[argv.GetArrayLength() - 1].GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return null;
    }

    static JsonDocument? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line, ReaderOptions);
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException) { return null; }
    }

    // The value of the first "type":"..." at or after `from`; `next` lands past it, -1 when there's none.
    static ReadOnlySpan<byte> TypeAfter(ReadOnlySpan<byte> s, int from, out int next)
    {
        next = -1;
        if (from >= s.Length) return default;
        int i = s[from..].IndexOf("\"type\":\""u8);
        if (i < 0) return default;
        int start = from + i + 8;
        int end = s[start..].IndexOf((byte)'"');
        if (end < 0) return default;
        next = start + end + 1;
        return s.Slice(start, end);
    }

    static DateTime Stamp(ReadOnlySpan<byte> head)
    {
        int i = head.IndexOf("\"timestamp\":\""u8);
        if (i >= 0)
        {
            var rest = head[(i + 13)..];
            int end = rest.IndexOf((byte)'"');
            if (end > 0 && DateTime.TryParse(Encoding.ASCII.GetString(rest[..end]), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                return t;
        }
        return DateTime.UtcNow;
    }

    static string? CallId(ReadOnlySpan<byte> head)
    {
        int i = head.IndexOf("\"call_id\":\""u8);
        if (i < 0) return null;
        var rest = head[(i + 11)..];
        int end = rest.IndexOf((byte)'"');
        return end > 0 ? Encoding.UTF8.GetString(rest[..end]) : null;
    }
}

sealed class CodexTui
{
    public CodexTui(int pid, long created, string? resumeId)
    {
        Pid = pid;
        Created = created;
        ResumeId = resumeId;
    }

    public int Pid { get; }
    public long Created { get; }
    public string? ResumeId { get; }
    public string? Title, TitleProject, Approval, StartThread;
    public string[] TitleNames = Array.Empty<string>();  // what may be the thread name, longest first
    public bool TitleKnown, TitleBusy, TitleAttention;
    public DateTime NextTitle, NextScreen, NextStartSearch, WaitingSince;
}

// Codex CLI sessions. A terminal `codex` is a TUI; the agent loop itself may run in a shared app-server
// daemon serving every TUI, so a thread's state comes from its rollout
// (~/.codex/sessions/YYYY/MM/DD/rollout-<local time>-<thread id>.jsonl), not from the TUI process.
// The TUI titles its terminal "<thread name> | <project>" with a spinner glyph in front during a turn.
// That title and session_index.jsonl (thread id -> name) tie a TUI to its thread, even after an in-app
// /resume moved it to another one.
sealed class CodexMonitor
{
    // subcommands that never put up a TUI; `codex exec` is how other agents call Codex
    static readonly HashSet<string> NotTui = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "e", "app-server", "app", "mcp", "mcp-server", "login", "logout", "completion", "sandbox", "debug",
        "apply", "a", "cloud", "cloud-tasks", "responses-api-proxy", "stdio-to-uds", "features", "proto", "generate-ts", "help",
    };

    static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "-m", "--model", "-c", "--config", "-p", "--profile", "-s", "--sandbox", "-a", "--ask-for-approval",
        "-C", "--cd", "-i", "--image", "--add-dir", "--local-provider", "--enable", "--disable",
    };

    // Approval prompts in the TUI's bottom pane, as spelled in the 0.166 binary. They never reach the rollout.
    const string RunPrompt = "Would you like to run the following command?";
    const string EditPrompt = "Would you like to make the following edits?";
    static readonly string[] Prompts =
    {
        RunPrompt, EditPrompt, "Would you like to grant these permissions?", "Do you want to approve network access",
        "Would you like to send input to", "needs your approval",
    };
    const string DeclineOption = "No, and tell Codex what to do differently";

    readonly string sessionsDir, indexPath, locksDir;
    readonly Dictionary<(int Pid, long Created), CodexTui> tuis = new();
    readonly HashSet<(int Pid, long Created)> notTuis = new();
    readonly Dictionary<string, CodexRollout> rollouts = new();
    readonly Dictionary<string, string> paths = new();  // thread id -> rollout file
    readonly Dictionary<string, (string Cwd, string? Originator)> metas = new();
    readonly Dictionary<string, DateTime> missing = new();  // thread id -> next look for its file
    Dictionary<string, (string Name, DateTime Updated)> names = new();
    (long Length, DateTime Written) indexStamp;

    public CodexMonitor()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(UserPaths.Home, ".codex");
        sessionsDir = Path.Combine(home, "sessions");
        indexPath = Path.Combine(home, "session_index.jsonl");
        locksDir = Path.Combine(home, "thread-writer-locks");
    }

    public bool AnyBusy { get; private set; }

    public void Scan(IReadOnlyDictionary<int, ProcEntry> procs)
    {
        var seen = new HashSet<(int, long)>();
        foreach (var p in procs.Values)
        {
            if (!p.Exe.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) continue;
            long created = Processes.CreationTime(p.Pid);
            if (created == 0) continue;
            var key = (p.Pid, created);
            seen.Add(key);
            if (tuis.ContainsKey(key) || notTuis.Contains(key)) continue;
            if (IsTui(Processes.CommandLine(p.Pid), out string? resumeId) && Processes.IsConsoleProgram(p.Pid))
                tuis[key] = new CodexTui(p.Pid, created, resumeId);
            else notTuis.Add(key);
        }
        foreach (var gone in tuis.Keys.Where(k => !seen.Contains(k)).ToList()) tuis.Remove(gone);
        notTuis.IntersectWith(seen);
    }

    public List<SessionView> Build(DateTime now)
    {
        var views = new List<SessionView>();
        AnyBusy = false;
        if (tuis.Count > 0) ReadIndex();
        var live = new List<CodexTui>();
        foreach (var (key, tui) in tuis.OrderBy(kv => kv.Key.Created).ToList())
        {
            if (!Processes.IsAlive(key.Pid, key.Created))
            {
                tuis.Remove(key);
                continue;
            }
            if (now >= tui.NextTitle) ReadTitle(tui, now);
            live.Add(tui);
        }

        // Threads a title or `codex resume <id>` names first, so a guess for an unnamed one can't take them.
        var threads = live.ToDictionary(t => t, t => ByName(t, now) ?? t.ResumeId);
        var used = threads.Values.OfType<string>().ToHashSet();
        foreach (var tui in live.Where(t => threads[t] == null))
            if (Unnamed(tui, used, now) is string thread)
            {
                threads[tui] = thread;
                used.Add(thread);
            }

        foreach (var tui in live)
        {
            string? thread = threads[tui];
            var view = Compose(tui, thread, thread != null ? Rollout(thread, now) : null, now);
            AnyBusy |= view.State != SessionState.Idle;
            views.Add(view);
        }
        foreach (string stale in rollouts.Keys.Where(k => !used.Contains(k)).ToList()) rollouts.Remove(stale);
        return views;
    }

    SessionView Compose(CodexTui tui, string? thread, CodexRollout? r, DateTime now)
    {
        string cwd = r?.Cwd ?? (thread != null ? Meta(thread, now)?.Cwd : null) ?? "";
        string project = cwd.Length > 0 ? SessionMonitor.ProjectName(cwd) : tui.TitleProject ?? "";
        string? name = thread != null && names.TryGetValue(thread, out var n) ? n.Name : null;
        // only trust the terminal title once it has Codex's "<name> | <project>" shape
        string title = name ?? (tui.TitleProject != null ? tui.TitleNames.LastOrDefault() : null) ?? (project.Length > 0 ? project : "Codex");

        var state = SessionState.Idle;
        string? kind = null, attention = null;
        DateTime since = DateTime.FromFileTimeUtc(tui.Created);
        if (r != null)
        {
            string? approval = Approval(tui, r, now);
            bool waiting = approval != null || r.Question != null || tui.TitleAttention;
            if (!waiting) tui.WaitingSince = default;
            else if (tui.WaitingSince == default) tui.WaitingSince = now;

            if (approval != null)
                (state, kind, attention, since) = (SessionState.Waiting, "permission", approval, tui.WaitingSince);
            else if (r.Question != null)
                (state, kind, attention, since) = (SessionState.Waiting, "question", r.Question, r.QuestionAt);
            else if (tui.TitleAttention)
                // the title says so even when the prompt is one we can't read off the screen
                (state, kind, attention, since) = (SessionState.Waiting, "other", "Codex needs you", tui.WaitingSince);
            else if (r.TurnOpen && (!tui.TitleKnown || tui.TitleBusy || now - r.LastLine < TimeSpan.FromMinutes(2)))
                (state, since) = (SessionState.Busy, r.TurnStarted);
            else
            {
                // turn over, or one the daemon never closed (it restarted mid-turn): no spinner, nothing written
                since = r.TurnEnded > DateTime.MinValue && !r.TurnOpen ? r.TurnEnded : r.LastLine > DateTime.MinValue ? r.LastLine : since;
                if (!r.TurnOpen && ToolText.TrailingQuestion(r.LastReply) is string question)
                    (state, kind, attention) = (SessionState.Asked, "asked", question);
            }
        }

        // Commands show as shell rows, like a Claude session's; anything else is the activity line.
        var items = new List<ItemView>();
        string? activity = null;
        DateTime activitySince = default;
        if (r != null && state is SessionState.Busy or SessionState.Waiting)
        {
            foreach (var call in r.Pending.Values.OrderBy(c => c.Started))
            {
                if (now - call.Started < TimeSpan.FromSeconds(1.5)) continue;  // skip blink-and-gone calls
                if (call.Command != null)
                    items.Add(new ItemView { Kind = ItemKind.Shell, Label = ToolText.FirstLine(call.Command) ?? "command", Command = call.Command, Started = call.Started });
                else if (state == SessionState.Busy && call.Name != "wait")
                {
                    activity = ToolText.Describe(call.Name, call.Summary);
                    activitySince = call.Started;
                }
            }
        }

        var tabTitles = new List<string>();
        if (tui.Title is { Length: > 0 } shown) tabTitles.Add(shown);
        if (name != null && project.Length > 0 && !tabTitles.Contains($"{name} | {project}")) tabTitles.Add($"{name} | {project}");

        return new SessionView
        {
            SessionId = thread ?? $"codex-{tui.Pid}",
            Pid = tui.Pid,
            Source = Cli.Codex,
            Title = title,
            TabTitles = tabTitles,
            Project = project,
            Cwd = cwd,
            State = state,
            StatusText = state switch { SessionState.Busy => "working", SessionState.Waiting => "needs you", SessionState.Asked => "asked", _ => "idle" },
            Since = since,
            Attention = attention,
            AttentionKind = kind,
            Activity = activity,
            ActivitySince = activitySince,
            Items = items,
            TranscriptPath = r?.Path,
            ProcStart = tui.Created,
        };
    }

    // Approval prompts only show on screen, and a turn waiting on one writes nothing. So once an open turn
    // has gone quiet, or the title reads "Action Required", look at the bottom of the TUI's screen every
    // couple of seconds.
    static string? Approval(CodexTui tui, CodexRollout r, DateTime now)
    {
        if (!tui.TitleAttention && (!r.TurnOpen || now - r.LastLine < TimeSpan.FromSeconds(2.5)))
        {
            tui.Approval = null;
            return null;
        }
        if (now < tui.NextScreen) return tui.Approval;
        tui.NextScreen = now.AddSeconds(2);
        tui.Approval = ConsoleInput.ReadScreen(tui.Pid) is string screen ? ApprovalOnScreen(screen) : null;
        return tui.Approval;
    }

    public static string? ApprovalOnScreen(string screen)
    {
        string[] lines = screen.Split('\n');
        int top = Math.Max(0, lines.Length - 24);  // the prompt sits in the bottom pane
        for (int i = lines.Length - 1; i >= top; i--)
        {
            string line = Unframe(lines[i]);
            string? prompt = Prompts.FirstOrDefault(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (prompt == null) continue;
            string? detail = DetailBelow(lines, i)?.TrimEnd('?', ' ');
            return prompt switch
            {
                RunPrompt => detail != null ? $"Allow command: {detail}?" : "Allow a command?",
                EditPrompt => detail != null ? $"Allow edits: {detail}?" : "Allow file edits?",
                _ => ToolText.Clip(line, 200),
            };
        }
        for (int i = lines.Length - 1; i >= top; i--)
            if (lines[i].Contains(DeclineOption, StringComparison.OrdinalIgnoreCase)) return "Codex wants your approval";
        return null;
    }

    // Under the prompt: "Environment: local", "Reason: ...", then "$ <command>" (wrapped), then the
    // numbered options. The command says the most; the reason stands in when there's none.
    static string? DetailBelow(string[] lines, int row)
    {
        string? reason = null;
        for (int i = row + 1; i < lines.Length && i <= row + 14; i++)
        {
            string text = Unframe(lines[i]);
            if (text.StartsWith("$ ", StringComparison.Ordinal)) return ToolText.Clip(text[2..].Trim(), 160);
            if (text.StartsWith("1. ", StringComparison.Ordinal)) break;
            if (reason == null && text.StartsWith("Reason:", StringComparison.OrdinalIgnoreCase)) reason = text[7..].Trim();
        }
        return reason is { Length: > 0 } ? ToolText.Clip(reason, 160) : null;
    }

    static string Unframe(string line) => line.Trim().Trim('│', '┃', '║', '▌', '▐', '•', '›', '>', '─', '━').Trim();

    string? ByName(CodexTui tui, DateTime now)
    {
        foreach (string name in tui.TitleNames)
        {
            var ids = names.Where(kv => kv.Value.Name == name).Select(kv => kv.Key).ToList();
            if (ids.Count == 0) continue;
            if (ids.Count > 1)
            {
                var here = ids.Where(id => string.Equals(SessionMonitor.ProjectName(Meta(id, now)?.Cwd ?? ""), tui.TitleProject, StringComparison.OrdinalIgnoreCase)).ToList();
                if (here.Count > 0) ids = here;
            }
            if (ids.Count > 1)
            {
                var loaded = ids.Where(IsLoaded).ToList();
                if (loaded.Count > 0) ids = loaded;
            }
            return ids.OrderByDescending(id => names[id].Updated).First();
        }
        return null;
    }

    // A thread too new to have a name. Codex writes the rollout with the first message, not at startup,
    // so take the earliest rollout started since this TUI was, in its project, that no other TUI has.
    string? Unnamed(CodexTui tui, HashSet<string> used, DateTime now)
    {
        if (tui.StartThread != null && used.Contains(tui.StartThread)) tui.StartThread = null;
        if (tui.StartThread == null && now >= tui.NextStartSearch)
        {
            tui.NextStartSearch = now.AddSeconds(3);
            tui.StartThread = FirstSince(tui, used, now);
        }
        return tui.StartThread;
    }

    string? FirstSince(CodexTui tui, HashSet<string> used, DateTime now)
    {
        DateTime start = DateTime.FromFileTime(tui.Created);  // rollout names are in local time
        string? best = null;
        DateTime bestAt = DateTime.MaxValue;
        for (DateTime day = start.Date; day <= DateTime.Now.Date; day = day.AddDays(1))
        {
            string dir = Path.Combine(sessionsDir, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
                {
                    if (!TryParseName(Path.GetFileName(file), out DateTime at, out string id)) continue;
                    if (at < start.AddSeconds(-2) || at >= bestAt || used.Contains(id)) continue;
                    paths[id] = file;
                    var meta = Meta(id, now);
                    // a codex exec run, or a session in another folder, isn't this TUI's
                    if (meta?.Originator is string origin && origin.Contains("exec", StringComparison.OrdinalIgnoreCase)) continue;
                    if (tui.TitleProject != null && meta != null &&
                        !string.Equals(SessionMonitor.ProjectName(meta.Value.Cwd), tui.TitleProject, StringComparison.OrdinalIgnoreCase))
                        continue;
                    (best, bestAt) = (id, at);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return best;
    }

    // rollout-2026-01-15T09-30-12-0199b0c4-5e6f-7a8b-9c0d-1e2f3a4b5c6d.jsonl
    static bool TryParseName(string name, out DateTime at, out string id)
    {
        at = default;
        id = "";
        if (name.Length != 70 || !name.StartsWith("rollout-", StringComparison.Ordinal)) return false;
        id = name.Substring(28, 36);
        return DateTime.TryParseExact(name.Substring(8, 19), "yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out at);
    }

    CodexRollout? Rollout(string thread, DateTime now)
    {
        if (!rollouts.TryGetValue(thread, out var r))
        {
            if (PathOf(thread, now) is not string path) return null;
            rollouts[thread] = r = new CodexRollout(path);
        }
        if (r.Update()) return r;
        rollouts.Remove(thread);
        paths.Remove(thread);
        return null;
    }

    string? PathOf(string thread, DateTime now)
    {
        if (paths.TryGetValue(thread, out var known)) return known;
        if (missing.TryGetValue(thread, out var retry) && now < retry) return null;
        try
        {
            if (Directory.Exists(sessionsDir))
                foreach (string file in Directory.EnumerateFiles(sessionsDir, $"rollout-*-{thread}.jsonl", SearchOption.AllDirectories))
                {
                    missing.Remove(thread);
                    return paths[thread] = file;
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        missing[thread] = now.AddSeconds(5);
        return null;
    }

    // cwd and originator from the rollout's first line, for telling same-named threads apart
    (string Cwd, string? Originator)? Meta(string thread, DateTime now)
    {
        if (metas.TryGetValue(thread, out var meta)) return meta;
        if (PathOf(thread, now) is not string path) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(fs.Length, 512 << 10)];
            int n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            int nl = Array.IndexOf(buffer, (byte)'\n', 0, n);
            if (nl < 0) return null;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, nl), new JsonReaderOptions { MaxDepth = 256 });
            using var doc = JsonDocument.ParseValue(ref reader);
            var p = Json.Get(doc.RootElement, "payload");
            return metas[thread] = (Json.Str(p, "cwd") ?? "", Json.Str(p, "originator"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The daemon keeps thread-writer-locks/<id>.lock open while it has a thread loaded.
    bool IsLoaded(string thread)
    {
        string path = Path.Combine(locksDir, thread + ".lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    void ReadIndex()
    {
        try
        {
            var fi = new FileInfo(indexPath);
            if (!fi.Exists)
            {
                names = new();
                return;
            }
            var stamp = (fi.Length, fi.LastWriteTimeUtc);
            if (stamp == indexStamp) return;
            indexStamp = stamp;
            // one line per rename: {"id":..., "thread_name":..., "updated_at":...}; the last one wins
            var map = new Dictionary<string, (string, DateTime)>();
            foreach (string line in Encoding.UTF8.GetString(Json.ReadShared(indexPath)).Split('\n'))
            {
                if (line.Length < 10) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (Json.Str(r, "id") is not string id || Json.Str(r, "thread_name") is not { Length: > 0 } name) continue;
                    DateTime.TryParse(Json.Str(r, "updated_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime updated);
                    map[id] = (name, updated);
                }
                catch (JsonException) { }
            }
            names = map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void ReadTitle(CodexTui tui, DateTime now)
    {
        tui.NextTitle = now.AddSeconds(3);
        string? raw = ConsoleInput.ReadTitle(tui.Pid);
        tui.TitleKnown = raw != null;
        if (raw == null) return;
        // "⠇ Fix flaky checkout test | storefront" during a turn, no glyph when idle, and
        // "[ . ] Action Required | Fix flaky checkout test | storefront" while it waits on you
        string t = raw.Trim();
        tui.Title = t.Length > 0 ? t : null;
        int space = t.IndexOf(' ');
        tui.TitleBusy = space is > 0 and <= 2 && t[0] != '[' && !t[..space].Any(char.IsLetterOrDigit);
        if (tui.TitleBusy) t = t[(space + 1)..].Trim();
        tui.TitleAttention = t.Contains("Action Required", StringComparison.OrdinalIgnoreCase);
        string[] parts = t.Split(" | ");
        // before its first message a session's title is the project alone
        tui.TitleProject = parts.Length >= 2 ? parts[^1].Trim() : t.Length > 0 && !TerminalFocus.IsShellTitle(t) ? t : null;
        // the name sits between an optional status prefix and the project; try it with and without
        tui.TitleNames = parts.Length >= 2
            ? Enumerable.Range(0, parts.Length - 1).Select(k => string.Join(" | ", parts[k..^1]).Trim()).ToArray()
            : Array.Empty<string>();
    }

    static bool IsTui(string? commandLine, out string? resumeId)
    {
        resumeId = null;
        if (commandLine == null) return false;
        var argv = SessionControl.SplitCommandLine(commandLine);
        for (int i = 1; i < argv.Count; i++)
        {
            string a = argv[i];
            if (a == "--") return true;
            if (a.StartsWith('-'))
            {
                if (ValueFlags.Contains(a)) i++;
                continue;
            }
            if (NotTui.Contains(a)) return false;
            if (a == "resume") resumeId = argv.Skip(i + 1).FirstOrDefault(x => Guid.TryParse(x, out _));
            return true;  // resume, fork, or a prompt to start on
        }
        return true;
    }
}
