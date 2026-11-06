using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace AIAnnounce;

sealed class AgyStep
{
    public int Idx, Type, Status, ParsedStatus = -1;
    public string? Tool, Args, Text;
    public DateTime FirstSeen;
}

// One conversation's trajectory store: conversations/<id>.db, a row per step, each with a type and a
// status. Only the newest steps and any unfinished ones are kept; payloads are parsed once per status.
sealed class AgyConversation
{
    // CortexStepStatus and CortexStepType values, read off the protobuf descriptors compiled into agy.exe
    public const int Pending = 1, Running = 2, Generating = 8, Waiting = 9, Queued = 11;
    public const int UserInput = 14, PlannerResponse = 15, NotifyUser = 82, AskQuestion = 138;
    const int Window = 48;

    readonly SortedDictionary<int, AgyStep> steps = new();
    bool metaRead;

    public AgyConversation(string id, string path)
    {
        Id = id;
        Path = path;
    }

    public string Id { get; }
    public string Path { get; }
    public string? Workspace { get; private set; }
    public List<(string Id, string Name, string? Type)> Subagents { get; } = new();
    public DateTime NextRefresh, ActiveSince;
    public bool WasActive;

    // the reply a finished turn ended on
    public string? FinalReply => steps.Count > 0 && steps.Values.Last() is { Type: PlannerResponse } last ? last.Text : null;

    public void Refresh(DateTime now)
    {
        if (!File.Exists(Path))
        {
            steps.Clear();
            return;
        }
        using var db = SqliteDb.OpenReadOnly(Path);
        if (db == null) return;
        long top = db.Scalar("select coalesce(max(idx), -1) from steps", -1);
        var current = new Dictionary<int, (int Type, int Status)>();
        // the newest steps, plus anything unfinished further back (a waiting step can sit behind newer ones)
        if (!db.Query("select idx, step_type, status from steps where idx > ?1 or status in (1, 2, 8, 9, 11)",
                r => current[(int)r.Int(0)] = ((int)r.Int(1), (int)r.Int(2)), top - Window))
            return;

        foreach (int gone in steps.Keys.Where(i => !current.ContainsKey(i)).ToList()) steps.Remove(gone);
        var parse = new List<int>();
        foreach (var (idx, (type, status)) in current)
        {
            if (!steps.TryGetValue(idx, out var step)) steps[idx] = step = new AgyStep { Idx = idx, FirstSeen = now };
            step.Type = type;
            step.Status = status;
            if (step.ParsedStatus != status) parse.Add(idx);
        }
        // tool results can carry whole images; the call itself sits at the front of the payload
        if (parse.Count > 0)
            db.Query("select idx, case when step_type = 15 then step_payload else substr(step_payload, 1, 16384) end, render_info " +
                     $"from steps where idx in ({string.Join(",", parse)})",
                r =>
                {
                    if (steps.TryGetValue((int)r.Int(0), out var step)) Parse(step, r.Blob(1), r.Blob(2));
                });
        if (!metaRead)
        {
            metaRead = true;
            db.Query("select data from trajectory_metadata_blob limit 1", r => Workspace = WorkspaceIn(r.Blob(0)));
        }
    }

    // Waiting on the user (and why), working (and on which step), or idle.
    public (SessionState State, string? Kind, string? Text, AgyStep? Current) Evaluate()
    {
        AgyStep? waiting = null, current = null;
        foreach (var s in steps.Values)
        {
            if (s.Status == Waiting) waiting = s;
            else if (s.Status is Pending or Running or Generating or Queued) current = s;
        }
        if (waiting != null)
        {
            if (waiting.Tool == "ask_question" || waiting.Type == AskQuestion)
                return (SessionState.Waiting, "question", AgyMonitor.Question(waiting.Args) ?? "agy has a question", waiting);
            if (waiting.Tool == "notify_user" || waiting.Type == NotifyUser)
                return (SessionState.Waiting, "plan", "Waiting for your review", waiting);
            return (SessionState.Waiting, "permission", waiting.Tool != null ? $"Allow {AgyMonitor.Describe(waiting)}?" : "Permission needed", waiting);
        }
        if (current != null) return (SessionState.Busy, null, null, current);
        // a prompt that hasn't produced a step yet
        if (steps.Count > 0 && steps.Values.Last().Type == UserInput) return (SessionState.Busy, null, null, null);
        return (SessionState.Idle, null, null, null);
    }

    void Parse(AgyStep s, byte[] payload, byte[] render)
    {
        s.ParsedStatus = s.Status;
        if (s.Type == PlannerResponse)
        {
            s.Text = Proto.Text(payload, 20, 1);  // the reply as shown; 20.3 is the model's thinking
            return;
        }
        // a tool step: payload 5 (metadata) -> 4 (the call) -> 1 call id, 2 tool name, 3 JSON arguments
        if (Proto.TryField(payload, out var call, 5, 4))
        {
            s.Tool = Proto.Text(call, 2);
            s.Args = Proto.Text(call, 3);
        }
        // invoke_subagent's render info names the store the subagent runs in: ..., <id>, <name>, <type>
        if (s.Tool == "invoke_subagent" && render.Length > 0)
        {
            var strings = new List<string>();
            Proto.Strings(render, strings, 0);
            int at = strings.FindIndex(x => x.Length == 36 && Guid.TryParse(x, out _));
            if (at >= 0 && !Subagents.Any(a => a.Id == strings[at]))
                Subagents.Add((strings[at], at + 1 < strings.Count ? strings[at + 1] : "subagent", at + 2 < strings.Count ? strings[at + 2] : null));
        }
    }

    static string? WorkspaceIn(byte[] blob)
    {
        var strings = new List<string>();
        Proto.Strings(blob, strings, 0);
        foreach (string s in strings)
            if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri))
                return uri.LocalPath;
        return null;
    }
}

sealed class AgyProcess
{
    public AgyProcess(int pid, long created)
    {
        Pid = pid;
        Created = created;
    }

    public int Pid { get; }
    public long Created { get; }
    public LineTail? Log;
    public string? ConversationId, ConsoleTitle, StateKey;
    public DateTime LastPrompt, Since, NextLogSearch, NextTitle;
}

// Antigravity CLI (agy) sessions. agy.exe runs its agent in-process and logs to
// ~/.gemini/antigravity-cli/log/cli-<start time>.log: the first line names the pid, later ones each
// conversation it opens and each prompt it forwards. The conversation's trajectory store says the rest.
// Subagents run in stores of their own, named by the invoke_subagent step that started them.
sealed class AgyMonitor
{
    static readonly Regex ConversationRx = new(
        @"(Streaming conversation|Created conversation|Forwarding user message to conversation) ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.Compiled);
    static readonly Regex LogNameRx = new(@"^cli-(\d{8}_\d{6})\.log$", RegexOptions.Compiled);
    static readonly Regex TitleRx = new(@"title:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    readonly string logDir, conversationsDir, annotationsDir;
    readonly Dictionary<(int Pid, long Created), AgyProcess> procs = new();
    readonly HashSet<(int Pid, long Created)> headless = new();
    readonly Dictionary<string, AgyConversation> conversations = new();
    readonly Dictionary<string, (string? Title, DateTime Next)> titles = new();

    public AgyMonitor()
    {
        string root = Path.Combine(UserPaths.Home, ".gemini", "antigravity-cli");
        logDir = Path.Combine(root, "log");
        conversationsDir = Path.Combine(root, "conversations");
        annotationsDir = Path.Combine(root, "annotations");
    }

    public bool AnyBusy { get; private set; }

    public void Scan(IReadOnlyDictionary<int, ProcEntry> snapshot)
    {
        var seen = new HashSet<(int, long)>();
        foreach (var p in snapshot.Values)
        {
            if (!p.Exe.Equals("agy.exe", StringComparison.OrdinalIgnoreCase)) continue;
            long created = Processes.CreationTime(p.Pid);
            if (created == 0) continue;
            var key = (p.Pid, created);
            seen.Add(key);
            if (procs.ContainsKey(key) || headless.Contains(key)) continue;
            if (Headless(Processes.CommandLine(p.Pid))) headless.Add(key);
            else procs[key] = new AgyProcess(p.Pid, created);
        }
        foreach (var gone in procs.Keys.Where(k => !seen.Contains(k)).ToList()) procs.Remove(gone);
        headless.IntersectWith(seen);
    }

    // `agy --print "..."` answers once and exits; other agents run it that way to make images
    static bool Headless(string? commandLine)
    {
        if (commandLine == null) return true;
        foreach (string a in SessionControl.SplitCommandLine(commandLine).Skip(1))
            if (a is "-p" or "--print" || a.StartsWith("--print=", StringComparison.Ordinal) || a.StartsWith("--output-format", StringComparison.Ordinal))
                return true;
        return false;
    }

    public List<SessionView> Build(DateTime now)
    {
        var views = new List<SessionView>();
        var live = new HashSet<string>();
        AnyBusy = false;
        foreach (var (key, p) in procs.OrderBy(kv => kv.Key.Created).ToList())
        {
            if (!Processes.IsAlive(key.Pid, key.Created))
            {
                procs.Remove(key);
                continue;
            }
            FollowLog(p, now);
            var view = Compose(p, now, live);
            AnyBusy |= view.State != SessionState.Idle;
            views.Add(view);
        }
        foreach (string stale in conversations.Keys.Where(k => !live.Contains(k)).ToList()) conversations.Remove(stale);
        return views;
    }

    SessionView Compose(AgyProcess p, DateTime now, HashSet<string> live)
    {
        AgyConversation? conv = null;
        if (p.ConversationId is string id)
        {
            conv = Conversation(id);
            live.Add(id);
            conv.Refresh(now);
        }
        var (state, kind, attention, current) = conv?.Evaluate() ?? (SessionState.Idle, null, null, null);

        // Subagents still at work show as agent rows; one stuck on the user makes the session need you.
        var items = new List<ItemView>();
        foreach (var sub in conv?.Subagents.TakeLast(6) ?? Enumerable.Empty<(string Id, string Name, string? Type)>())
        {
            var sc = Conversation(sub.Id);
            live.Add(sub.Id);
            if (now >= sc.NextRefresh)
            {
                sc.Refresh(now);
                sc.NextRefresh = now.AddSeconds(sc.WasActive ? 1 : 10);
            }
            var (subState, subKind, subText, subCurrent) = sc.Evaluate();
            bool active = subState != SessionState.Idle;
            if (active && !sc.WasActive) sc.ActiveSince = now;
            sc.WasActive = active;
            if (!active) continue;
            items.Add(new ItemView
            {
                Kind = ItemKind.Agent,
                Label = sub.Name,
                Detail = sub.Type,
                Activity = subCurrent?.Tool != null ? Describe(subCurrent) : null,
                Started = sc.ActiveSince,
            });
            if (subState == SessionState.Waiting && state != SessionState.Waiting)
                (state, kind, attention) = (SessionState.Waiting, subKind, $"Agent \"{sub.Name}\": {subText}");
        }
        // the turn is over but the subagents it started aren't
        if (state == SessionState.Idle && items.Count > 0) state = SessionState.Busy;
        if (state == SessionState.Idle && ToolText.TrailingQuestion(conv?.FinalReply) is string question)
            (state, kind, attention) = (SessionState.Asked, "asked", question);

        string key = $"{state}|{attention}";
        if (p.StateKey != key)
        {
            // first sight: date it from the last prompt rather than from when we started watching
            DateTime first = state == SessionState.Waiting ? now
                : p.LastPrompt > DateTime.MinValue ? p.LastPrompt
                : DateTime.FromFileTimeUtc(p.Created);
            p.Since = p.StateKey == null ? first : now;
            p.StateKey = key;
        }

        string cwd = conv?.Workspace ?? "";
        string project = cwd.Length > 0 ? SessionMonitor.ProjectName(cwd) : "";
        string? name = conv != null ? TitleOf(conv.Id, now) : null;
        string title = name ?? (project.Length > 0 ? project : "agy");

        if (now >= p.NextTitle)
        {
            p.NextTitle = now.AddSeconds(10);
            p.ConsoleTitle = ConsoleInput.ReadTitle(p.Pid);
        }
        var tabTitles = new List<string>();
        if (p.ConsoleTitle is { Length: > 0 } shown && !TerminalFocus.IsShellTitle(shown)) tabTitles.Add(shown);
        if (name != null && !tabTitles.Contains(name)) tabTitles.Add(name);

        bool showActivity = state == SessionState.Busy && current?.Tool != null;
        return new SessionView
        {
            SessionId = conv?.Id ?? $"agy-{p.Pid}",
            Pid = p.Pid,
            Source = Cli.Agy,
            Title = title,
            TabTitles = tabTitles,
            Project = project,
            Cwd = cwd,
            State = state,
            StatusText = state switch { SessionState.Busy => "working", SessionState.Waiting => "needs you", SessionState.Asked => "asked", _ => "idle" },
            Since = p.Since,
            Attention = attention,
            AttentionKind = kind,
            Activity = showActivity ? Describe(current!) : null,
            ActivitySince = showActivity ? current!.FirstSeen : default,
            Items = items,
            TranscriptPath = conv?.Path,
            ProcStart = p.Created,
        };
    }

    AgyConversation Conversation(string id)
    {
        if (!conversations.TryGetValue(id, out var c))
            conversations[id] = c = new AgyConversation(id, Path.Combine(conversationsDir, id + ".db"));
        return c;
    }

    void FollowLog(AgyProcess p, DateTime now)
    {
        if (p.Log == null)
        {
            if (now < p.NextLogSearch) return;
            p.NextLogSearch = now.AddSeconds(3);
            p.Log = FindLog(p);
            if (p.Log == null) return;
        }
        if (!p.Log.Read(line => OnLogLine(p, line))) p.Log = null;
    }

    // "I0115 09:30:12.913870     873 conversation_manager.go:1093] Streaming conversation 6c1f0e2a-..."
    static void OnLogLine(AgyProcess p, ReadOnlySpan<byte> line)
    {
        if (line.IndexOf("conversation "u8) < 0) return;
        string text = Encoding.UTF8.GetString(line);
        var m = ConversationRx.Match(text);
        if (!m.Success) return;
        p.ConversationId = m.Groups[2].Value;
        if (m.Groups[1].Value.StartsWith("Forwarding", StringComparison.Ordinal) && LogTime(text) is DateTime at) p.LastPrompt = at;
    }

    static DateTime? LogTime(string line)
    {
        if (line.Length < 21 || !DateTime.TryParseExact(line.Substring(1, 4) + line.Substring(6, 8), "MMddHH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return null;
        // glog leaves the year out; a stamp "ahead" of now is from last year
        if (local > DateTime.Now.AddDays(1)) local = local.AddYears(-1);
        return local.ToUniversalTime();
    }

    // log/cli-20260115_093012.log, named for the local time the process started; its first line names the pid
    LineTail? FindLog(AgyProcess p)
    {
        DateTime started = DateTime.FromFileTime(p.Created);
        var pidRx = new Regex($@"with pid {p.Pid}\b");
        try
        {
            if (!Directory.Exists(logDir)) return null;
            foreach (string file in Directory.EnumerateFiles(logDir, "cli-*.log"))
            {
                var m = LogNameRx.Match(System.IO.Path.GetFileName(file));
                if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var at) || Math.Abs((at - started).TotalSeconds) > 120)
                    continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var head = new byte[(int)Math.Min(fs.Length, 4096)];
                int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (pidRx.IsMatch(Encoding.UTF8.GetString(head, 0, n))) return new LineTail(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    // annotations/<id>.pbtxt: title:"Refactor billing module"
    string? TitleOf(string id, DateTime now)
    {
        if (titles.TryGetValue(id, out var cached) && now < cached.Next) return cached.Title;
        string? title = null;
        try
        {
            string path = Path.Combine(annotationsDir, id + ".pbtxt");
            if (File.Exists(path) && TitleRx.Match(Encoding.UTF8.GetString(Json.ReadShared(path))) is { Success: true } m)
                title = Unescape(m.Groups[1].Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        titles[id] = (string.IsNullOrWhiteSpace(title) ? null : title, now.AddSeconds(5));
        return titles[id].Title;
    }

    // text-format protobuf escapes: \" \\ \n, and \NNN octal for each byte of non-ASCII UTF-8
    static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length)
            {
                int width = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(s.Substring(i, width)));
                i += width - 1;
                continue;
            }
            char e = s[++i];
            if (e is >= '0' and <= '7')
            {
                int v = e - '0';
                for (int k = 0; k < 2 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7'; k++) v = v * 8 + (s[++i] - '0');
                bytes.Add((byte)v);
            }
            else bytes.Add((byte)(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e }));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static string? Question(string? args)
    {
        if (args == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(args);
            return ToolText.QuestionIn(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    public static string Describe(AgyStep s)
    {
        string tool = s.Tool ?? "tool";
        string summary = "";
        if (s.Args != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(s.Args);
                summary = ToolText.Summarize(tool, doc.RootElement);
            }
            catch (JsonException) { }
        }
        return ToolText.Describe(tool, summary);
    }
}

// Enough protobuf to read agy's step blobs without their schema: walk to a field by number, or collect
// every string-looking field in order.
static class Proto
{
    public static bool TryField(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> value, params int[] path)
    {
        value = data;
        foreach (int field in path)
            if (!TryFind(value, field, out value)) return false;
        return true;
    }

    public static string? Text(ReadOnlySpan<byte> data, params int[] path) =>
        TryField(data, out var value, path) && Utf8.IsValid(value) ? Encoding.UTF8.GetString(value) : null;

    public static void Strings(ReadOnlySpan<byte> data, List<string> into, int depth)
    {
        int i = 0;
        while (i < data.Length && into.Count < 256)
        {
            if (!Next(data, ref i, out _, out int wire, out var chunk)) return;
            if (wire != 2 || chunk.Length == 0) continue;
            if (LooksLikeText(chunk)) into.Add(Encoding.UTF8.GetString(chunk));
            else if (depth < 8) Strings(chunk, into, depth + 1);
        }
    }

    static bool TryFind(ReadOnlySpan<byte> data, int field, out ReadOnlySpan<byte> value)
    {
        int i = 0;
        while (i < data.Length)
        {
            if (!Next(data, ref i, out int number, out int wire, out value)) break;
            if (wire == 2 && number == field) return true;
        }
        value = default;
        return false;
    }

    // One field at i. A length-delimited field hands back its bytes, cut short if the blob was; other
    // wire types are skipped over.
    static bool Next(ReadOnlySpan<byte> data, scoped ref int i, out int number, out int wire, out ReadOnlySpan<byte> chunk)
    {
        chunk = default;
        wire = 0;
        number = 0;
        if (!Varint(data, ref i, out ulong key)) return false;
        number = (int)(key >> 3);
        wire = (int)(key & 7);
        if (number == 0) return false;
        switch (wire)
        {
            case 0:
                return Varint(data, ref i, out _);
            case 1:
                i += 8;
                return i <= data.Length;
            case 5:
                i += 4;
                return i <= data.Length;
            case 2:
                if (!Varint(data, ref i, out ulong length)) return false;
                int take = (int)Math.Min(length, (ulong)(data.Length - i));
                chunk = data.Slice(i, take);
                i += take;
                return true;
            default:
                return false;  // groups; not used in these blobs
        }
    }

    static bool Varint(ReadOnlySpan<byte> data, ref int i, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            byte b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }

    static bool LooksLikeText(ReadOnlySpan<byte> chunk)
    {
        if (!Utf8.IsValid(chunk)) return false;
        foreach (byte b in chunk)
            if (b < 0x20 && b is not ((byte)'\n' or (byte)'\r' or (byte)'\t')) return false;
        return true;
    }
}
