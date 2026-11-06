using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIAnnounce;

sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTime Started { get; init; }
    public string Summary { get; set; } = "";
    public string? Description { get; set; }
    public string? Command { get; set; }
    public string? SubagentType { get; set; }
    public string? Question { get; set; }
    public string? StopTarget { get; set; }  // task id named by TaskStop / KillShell
}

sealed class BgTask
{
    public required string Id { get; init; }
    public required bool IsAgent { get; init; }
    public required string Label { get; init; }
    public string? Command { get; init; }
    public string? AgentType { get; init; }
    public DateTime Started { get; init; }
}

sealed record AgentRun(string Id, string Label, string? Type, string? Activity, DateTime Started);

sealed class AgentFile
{
    public AgentFile(string id) => Id = id;
    public string Id { get; }
    public string? AgentType, Description, ToolUseId, Activity;
    public bool Finished;
    public DateTime Created, MetaWrite, LogWrite, ActivityStamp;
}

// Follows one session's JSONL transcript from the end. Some transcripts run into gigabytes, so only
// the last few MB are read at startup and then just the bytes appended since the previous poll.
sealed class TranscriptTracker
{
    const int InitialTail = 6 << 20;
    const int ChunkSize = 1 << 20;
    const int MaxLine = 48 << 20;

    static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 512 };
    static readonly Regex NotificationRx = new(@"<task-notification>(.*?)</task-notification>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex TaskIdRx = new(@"<task-id>([^<]+)</task-id>", RegexOptions.Compiled);
    static readonly Regex StatusRx = new(@"<status>([^<]+)</status>", RegexOptions.Compiled);

    readonly string projectsDir;
    readonly string cwd;
    DateTime nextLocate;
    long offset = -1;
    bool skipping;
    MemoryStream carry = new();

    readonly HashSet<string> closedTasks = new();
    readonly Dictionary<string, AgentFile> agentFiles = new();
    string? subagentsDir;
    DateTime nextAgentScan;

    public TranscriptTracker(string projectsDir, string sessionId, string cwd)
    {
        this.projectsDir = projectsDir;
        this.cwd = cwd;
        SessionId = sessionId;
    }

    public string SessionId { get; }
    public string? TranscriptPath { get; private set; }
    public Dictionary<string, ToolCall> Pending { get; } = new();
    public Dictionary<string, BgTask> Tasks { get; } = new();
    public string? LastText { get; private set; }
    public bool EndedOnText { get; private set; }
    public string? AiTitle { get; private set; }
    public string? CustomTitle { get; private set; }

    public void Update()
    {
        if (TranscriptPath == null)
        {
            if (DateTime.UtcNow < nextLocate) return;
            nextLocate = DateTime.UtcNow.AddSeconds(2);
            TranscriptPath = Locate();
            if (TranscriptPath == null) return;
        }

        try
        {
            using var fs = new FileStream(TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (offset < 0 || length < offset)
            {
                if (length < offset) Reset();
                offset = Math.Max(0, length - InitialTail);
                skipping = offset > 0;  // landed mid-line, drop bytes up to the next newline
            }
            if (length > offset) ReadFrom(fs, length);
        }
        catch (FileNotFoundException) { TranscriptPath = null; }
        catch (DirectoryNotFoundException) { TranscriptPath = null; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A turn that ended can't still have tools in flight; clears calls whose result we never saw.
    public void DropPendingBefore(DateTime idleSince)
    {
        foreach (var id in Pending.Values.Where(c => c.Started < idleSince.AddSeconds(-1)).Select(c => c.Id).ToList())
            Pending.Remove(id);
    }

    public List<AgentRun> RunningAgents(DateTime processStart, DateTime now)
    {
        ScanAgentFiles(now);
        var runs = new List<AgentRun>();
        var seen = new HashSet<string>();

        foreach (var task in Tasks.Values)
        {
            if (!task.IsAgent || task.Started < processStart.AddSeconds(-5)) continue;
            agentFiles.TryGetValue(task.Id, out var file);
            if (file?.Finished == true || !seen.Add(task.Id)) continue;
            runs.Add(new AgentRun(task.Id, task.Label, task.AgentType ?? file?.AgentType, ActivityOf(file), task.Started));
        }

        // Foreground agents: the Agent tool call is still open
        foreach (var call in Pending.Values)
        {
            if (call.Name is not ("Agent" or "Task")) continue;
            var file = agentFiles.Values.FirstOrDefault(a => a.ToolUseId == call.Id);
            if (file != null && (file.Finished || !seen.Add(file.Id))) continue;
            runs.Add(new AgentRun(file?.Id ?? call.Id, call.Description ?? file?.Description ?? "agent",
                call.SubagentType ?? file?.AgentType, ActivityOf(file), call.Started));
        }

        // Agents launched before the transcript window we read; trust the meta file plus recent log writes
        foreach (var file in agentFiles.Values)
        {
            if (file.Finished || seen.Contains(file.Id) || closedTasks.Contains(file.Id)) continue;
            if (file.Created < processStart || now - file.LogWrite > TimeSpan.FromMinutes(10)) continue;
            if (runs.Any(r => r.Id == file.Id)) continue;
            seen.Add(file.Id);
            runs.Add(new AgentRun(file.Id, file.Description ?? "agent", file.AgentType, ActivityOf(file), file.Created));
        }
        return runs;
    }

    string? Locate()
    {
        string slug = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
        string direct = Path.Combine(projectsDir, slug, SessionId + ".jsonl");
        if (File.Exists(direct)) return direct;
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(projectsDir))
            {
                string candidate = Path.Combine(dir, SessionId + ".jsonl");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    void Reset()
    {
        Pending.Clear();
        Tasks.Clear();
        closedTasks.Clear();
        LastText = null;
        EndedOnText = false;
        carry = new MemoryStream();
    }

    void ReadFrom(FileStream fs, long length)
    {
        fs.Position = offset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            while (offset < length)
            {
                int n = fs.Read(buffer, 0, (int)Math.Min(ChunkSize, length - offset));
                if (n <= 0) break;
                offset += n;
                int start = 0;
                while (start < n)
                {
                    int nl = Array.IndexOf(buffer, (byte)'\n', start, n - start);
                    if (nl < 0) break;
                    EndLine(buffer, start, nl - start);
                    start = nl + 1;
                }
                Carry(buffer, start, n - start);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    void EndLine(byte[] buffer, int start, int count)
    {
        if (skipping)
        {
            skipping = false;
            carry.SetLength(0);
            return;
        }
        if (carry.Length == 0)
        {
            Line(new ReadOnlyMemory<byte>(buffer, start, count));
            return;
        }
        carry.Write(buffer, start, count);
        Line(new ReadOnlyMemory<byte>(carry.GetBuffer(), 0, (int)carry.Length));
        // one tracker per session, each idle most of the time: don't sit on a big buffer
        if (carry.Capacity > 256 << 10) carry = new MemoryStream();
        else carry.SetLength(0);
    }

    void Carry(byte[] buffer, int start, int count)
    {
        if (skipping || count == 0) return;
        if (carry.Length + count > MaxLine)
        {
            skipping = true;
            carry = new MemoryStream();
            return;
        }
        carry.Write(buffer, start, count);
    }

    void Line(ReadOnlyMemory<byte> line)
    {
        var span = line.Span;
        if (span.Length < 16) return;
        if (span.StartsWith("{\"type\":\"ai-title\""u8) || span.StartsWith("{\"type\":\"custom-title\""u8))
        {
            OnTitle(line);
            return;
        }
        bool interesting = span.IndexOf("\"role\":\"assistant\""u8) >= 0
                           || span.IndexOf("\"role\":\"user\""u8) >= 0
                           || span.IndexOf("<task-notification>"u8) >= 0;
        if (!interesting) return;

        try
        {
            using var doc = JsonDocument.Parse(line, DocOptions);
            var root = doc.RootElement;
            if (Json.Bool(root, "isSidechain")) return;
            switch (Json.Str(root, "type"))
            {
                case "assistant": OnAssistant(root); break;
                case "user": OnUser(root); break;
                case "queue-operation":
                    if (Json.Str(root, "content") is string content) OnNotifications(content);
                    break;
            }
        }
        catch (JsonException) { }
    }

    // {"type":"ai-title","aiTitle":"..."} / {"type":"custom-title","customTitle":"..."}
    void OnTitle(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, DocOptions);
            var root = doc.RootElement;
            if (Json.Str(root, "aiTitle") is { Length: > 0 } ai) AiTitle = ai;
            if (Json.Str(root, "customTitle") is { Length: > 0 } custom) CustomTitle = custom;
        }
        catch (JsonException) { }
    }

    void OnAssistant(JsonElement root)
    {
        var content = Json.Get(Json.Get(root, "message"), "content");
        if (content.ValueKind != JsonValueKind.Array) return;
        DateTime at = Json.Timestamp(root);
        foreach (var block in content.EnumerateArray())
        {
            switch (Json.Str(block, "type"))
            {
                case "text":
                    string? text = Json.Str(block, "text");
                    if (string.IsNullOrWhiteSpace(text)) break;
                    LastText = text;
                    EndedOnText = true;
                    break;
                case "tool_use":
                    AddCall(block, at);
                    EndedOnText = false;
                    break;
            }
        }
    }

    void AddCall(JsonElement block, DateTime at)
    {
        string? id = Json.Str(block, "id"), name = Json.Str(block, "name");
        if (id == null || name == null) return;
        var input = Json.Get(block, "input");
        var call = new ToolCall { Id = id, Name = name, Started = at, Description = Json.Str(input, "description") };
        switch (name)
        {
            case "Bash":
            case "PowerShell":
                call.Command = Json.Str(input, "command");
                break;
            case "Agent":
            case "Task":
                call.SubagentType = Json.Str(input, "subagent_type");
                break;
            case "AskUserQuestion":
                call.Question = ToolText.FirstQuestion(input);
                break;
            case "TaskStop":
            case "KillShell":
            case "KillBash":
                call.StopTarget = Json.Str(input, "task_id") ?? Json.Str(input, "shell_id") ?? Json.Str(input, "bash_id");
                break;
        }
        call.Summary = ToolText.Summarize(name, input);
        Pending[id] = call;
    }

    void OnUser(JsonElement root)
    {
        var content = Json.Get(Json.Get(root, "message"), "content");
        if (content.ValueKind == JsonValueKind.String)
        {
            OnPrompt(content.GetString(), root);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        bool sawResult = false;
        string? firstText = null;
        foreach (var block in content.EnumerateArray())
        {
            string? type = Json.Str(block, "type");
            if (type == "tool_result")
            {
                sawResult = true;
                OnResult(Json.Str(block, "tool_use_id"), Json.Get(root, "toolUseResult"), Json.Timestamp(root));
            }
            else if (type == "text") firstText ??= Json.Str(block, "text");
        }
        if (!sawResult) OnPrompt(firstText, root);
    }

    void OnPrompt(string? text, JsonElement root)
    {
        if (Json.Bool(root, "isMeta")) return;
        if (text != null)
        {
            if (text.Contains("<task-notification>", StringComparison.Ordinal))
            {
                OnNotifications(text);
                return;
            }
            if (text.StartsWith("<command-", StringComparison.Ordinal) ||
                text.StartsWith("<local-command", StringComparison.Ordinal) ||
                text.StartsWith("<bash-", StringComparison.Ordinal))
                return;
        }
        // The user answered; whatever Claude asked last is no longer open.
        LastText = null;
        EndedOnText = false;
    }

    void OnResult(string? toolUseId, JsonElement result, DateTime at)
    {
        if (toolUseId == null) return;
        Pending.Remove(toolUseId, out var call);

        if (result.ValueKind == JsonValueKind.Object)
        {
            if (Json.Str(result, "backgroundTaskId") is string shellId && !closedTasks.Contains(shellId))
            {
                Tasks[shellId] = new BgTask
                {
                    Id = shellId,
                    IsAgent = false,
                    Label = call?.Description ?? ToolText.FirstLine(call?.Command) ?? "background command",
                    Command = call?.Command,
                    Started = call?.Started ?? at,
                };
            }

            if (Json.Str(result, "agentId") is string agentId && !closedTasks.Contains(agentId) &&
                (Json.Bool(result, "isAsync") || Json.Str(result, "status") == "async_launched"))
            {
                Tasks[agentId] = new BgTask
                {
                    Id = agentId,
                    IsAgent = true,
                    Label = Json.Str(result, "description") ?? call?.Description ?? "agent",
                    AgentType = call?.SubagentType,
                    Started = call?.Started ?? at,
                };
            }
        }

        if (call?.StopTarget is string target) Close(target);
    }

    void OnNotifications(string text)
    {
        foreach (Match m in NotificationRx.Matches(text))
        {
            string body = m.Groups[1].Value;
            string? id = TaskIdRx.Match(body) is { Success: true } t ? t.Groups[1].Value.Trim() : null;
            string status = StatusRx.Match(body) is { Success: true } s ? s.Groups[1].Value.Trim() : "";
            if (id != null && status is not ("running" or "started" or "progress")) Close(id);
        }
    }

    void Close(string taskId)
    {
        Tasks.Remove(taskId);
        closedTasks.Add(taskId);
    }

    void ScanAgentFiles(DateTime now)
    {
        if (TranscriptPath == null || now < nextAgentScan) return;
        nextAgentScan = now.AddSeconds(2);
        subagentsDir ??= Path.Combine(Path.ChangeExtension(TranscriptPath, null)!, "subagents");
        var dir = new DirectoryInfo(subagentsDir);
        if (!dir.Exists) return;
        try
        {
            foreach (var fi in dir.EnumerateFiles("agent-*"))
            {
                string n = fi.Name;
                if (n.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    var a = AgentFileFor(n[6..^10]);
                    if (a.MetaWrite == fi.LastWriteTimeUtc) continue;
                    a.MetaWrite = fi.LastWriteTimeUtc;
                    a.Created = fi.CreationTimeUtc;
                    ReadMeta(a, fi.FullName);
                }
                else if (n.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    AgentFileFor(n[6..^6]).LogWrite = fi.LastWriteTimeUtc;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    AgentFile AgentFileFor(string id)
    {
        if (!agentFiles.TryGetValue(id, out var a)) agentFiles[id] = a = new AgentFile(id);
        return a;
    }

    static void ReadMeta(AgentFile a, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(Json.ReadShared(path));
            var r = doc.RootElement;
            a.AgentType = Json.Str(r, "agentType");
            a.Description = Json.Str(r, "description");
            a.ToolUseId = Json.Str(r, "toolUseId");
            a.Finished = Json.Bool(r, "finished");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    string? ActivityOf(AgentFile? file)
    {
        if (file == null || subagentsDir == null) return null;
        if (file.ActivityStamp != file.LogWrite)
        {
            file.ActivityStamp = file.LogWrite;
            file.Activity = LastToolCall(Path.Combine(subagentsDir, $"agent-{file.Id}.jsonl")) ?? file.Activity;
        }
        return file.Activity;
    }

    static string? LastToolCall(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int take = (int)Math.Min(fs.Length, 256 * 1024);
            fs.Position = fs.Length - take;
            var buffer = new byte[take];
            fs.ReadExactly(buffer);
            int end = take;
            while (end > 0)
            {
                int nl = Array.LastIndexOf(buffer, (byte)'\n', end - 1);
                var line = new ReadOnlyMemory<byte>(buffer, nl + 1, end - nl - 1);
                if (line.Span.IndexOf("\"tool_use\""u8) >= 0 && line.Span.IndexOf("\"role\":\"assistant\""u8) >= 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line, DocOptions);
                        var content = Json.Get(Json.Get(doc.RootElement, "message"), "content");
                        if (content.ValueKind == JsonValueKind.Array)
                        {
                            string? found = null;
                            foreach (var block in content.EnumerateArray())
                                if (Json.Str(block, "type") == "tool_use" && Json.Str(block, "name") is string name)
                                    found = ToolText.Describe(name, ToolText.Summarize(name, Json.Get(block, "input")));
                            if (found != null) return found;
                        }
                    }
                    catch (JsonException) { }
                }
                if (nl < 0) break;
                end = nl;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

static class ToolText
{
    public static string Summarize(string name, JsonElement input)
    {
        string? s = name switch
        {
            "Bash" or "PowerShell" => Json.Str(input, "description") ?? Json.Str(input, "command"),
            "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" =>
                FileName(Json.Str(input, "file_path") ?? Json.Str(input, "notebook_path")),
            "Grep" or "Glob" => Json.Str(input, "pattern"),
            "WebFetch" => Host(Json.Str(input, "url")),
            "WebSearch" => Json.Str(input, "query"),
            "Agent" or "Task" => Json.Str(input, "description"),
            "Skill" => Json.Str(input, "skill"),
            "AskUserQuestion" => FirstQuestion(input),
            _ => FirstStringValue(input),
        };
        return Clip(FirstLine(s) ?? "", 160);
    }

    public static string Describe(string name, string summary) =>
        summary.Length == 0 ? PrettyName(name) : $"{PrettyName(name)}: {summary}";

    public static string Describe(ToolCall call) =>
        Describe(call.Name, call.Name is "Bash" or "PowerShell" ? FirstLine(call.Command) ?? call.Summary : call.Summary);

    public static string PrettyName(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
        string[] parts = name.Split("__");
        return parts.Length >= 3
            ? $"{parts[1].Replace("claude_ai_", "")}/{string.Join("__", parts.Skip(2))}"
            : name;
    }

    public static bool IsShellOrAgent(string name) =>
        name is "Bash" or "PowerShell" or "Agent" or "Task" or "AskUserQuestion" or "ExitPlanMode";

    public static string? FirstQuestion(JsonElement input)
    {
        var questions = Json.Get(input, "questions");
        if (questions.ValueKind == JsonValueKind.Array)
            foreach (var q in questions.EnumerateArray())
                if (Json.Str(q, "question") is string text) return text;
        return Json.Str(input, "question");
    }

    // Claude, Codex and agy each spell a question's text differently
    static readonly string[] QuestionKeys = { "question", "title", "text", "prompt", "header" };

    public static string? QuestionIn(JsonElement input)
    {
        var questions = Json.Get(input, "questions");
        if (questions.ValueKind == JsonValueKind.Array)
            foreach (var q in questions.EnumerateArray())
                foreach (string key in QuestionKeys)
                    if (Json.Str(q, key) is { Length: > 0 } text) return text;
        return Json.Str(input, "question");
    }

    public static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (string line in s.Split('\n'))
            if (line.Trim() is { Length: > 0 } t) return Clip(t, 300);
        return null;
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // Transcript commands vs. what we recovered from a live command line: ignore quoting and whitespace.
    public static bool SameCommand(string? fromTranscript, string? fromProcess)
    {
        if (fromTranscript == null || fromProcess == null) return false;
        string a = Squash(fromTranscript), b = Squash(fromProcess);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || (a.Length >= 12 && b.Length > a.Length && b.Contains(a, StringComparison.Ordinal));
    }

    static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c) && c is not ('"' or '\'' or '\\' or '`')) sb.Append(c);
        return sb.ToString();
    }

    // Reply ended with a question for the user ("Want me to also update the docs?")
    public static string? TrailingQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = new List<string>();
        bool fence = false;
        foreach (string raw in text.Split('\n'))
        {
            string l = raw.Trim();
            if (l.StartsWith("```", StringComparison.Ordinal)) { fence = !fence; continue; }
            // a blockquote is someone else's words, like a drafted message whose question is for its recipient
            if (!fence && l.Length > 0 && !l.StartsWith('>')) lines.Add(l);
        }
        for (int i = lines.Count - 1; i >= 0 && i >= lines.Count - 2; i--)
        {
            string line = StripMarkdown(lines[i]);
            if (line.TrimEnd(' ', ')', '"', '\'', '”', '’').EndsWith('?'))
                return LastSentences(line);
        }
        return null;
    }

    static string StripMarkdown(string line)
    {
        line = Regex.Replace(line, @"^(\s*[-*+>]\s+|\s*\d+[.)]\s+|#+\s+)", "");
        return line.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
    }

    static string LastSentences(string s)
    {
        var cuts = new List<int>();
        for (int i = s.Length - 2; i > 0; i--)
            if ((s[i] is '.' or '!' or '?') && s[i + 1] == ' ') cuts.Add(i + 1);
        if (cuts.Count == 0) return s;
        string last = s[cuts[0]..].Trim();
        // "Or B?" on its own reads badly; keep the sentence before it
        if (last.Length < 30 && cuts.Count > 1) last = s[cuts[1]..].Trim();
        return last;
    }

    static string? FileName(string? path) => string.IsNullOrEmpty(path) ? null : Path.GetFileName(path.TrimEnd('/', '\\'));

    static string? Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath.TrimEnd('/') : url;

    static string? FirstStringValue(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in input.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) return v;
        return null;
    }
}
