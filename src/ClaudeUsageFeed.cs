using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIAnnounce;

// Claude Code hands its status-line command a rate_limits object (five_hour / seven_day, each with
// used_percentage and resets_at in epoch seconds), taken from the headers of the session's last
// API reply. Reading it costs no request and can't be rate-limited, so Session and Week stay live
// while the usage endpoint is answering 429. Model-scoped limits (Fable wk) aren't in it; those
// still come from the endpoint. Only the two windows are stored, never session or account details.
sealed class ClaudeUsageFeed
{
    public const string Flag = "--claude-statusline";
    readonly string settingsPath;
    readonly string samplePath;

    public sealed record Window(double Percent, DateTime? ResetsAt);
    public sealed record Sample(DateTime At, Window? Session, Window? Week);

    public ClaudeUsageFeed(string claudeDir) : this(Path.Combine(claudeDir, "settings.json"), Path.Combine(Settings.Dir, "claude-statusline.json")) { }

    internal ClaudeUsageFeed(string settingsPath, string samplePath)
    {
        this.settingsPath = settingsPath;
        this.samplePath = samplePath;
    }

    public bool Connected
    {
        get
        {
            try
            {
                if (!File.Exists(settingsPath)) return false;
                using var doc = JsonDocument.Parse(Json.ReadShared(settingsPath));
                return Json.Str(Json.Get(doc.RootElement, "statusLine"), "command")?.Contains(Flag, StringComparison.Ordinal) ?? false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }

    // Claude runs the command through a shell (Git Bash or cmd). Forward slashes work in both, and
    // a path either shell would expand is refused rather than quoted half-right.
    public static string? CommandFor(string executable)
    {
        executable = Path.GetFullPath(executable).Replace('\\', '/');
        return executable.IndexOfAny(new[] { '%', '"', '$', '`' }) >= 0 ? null : $"\"{executable}\" {Flag}";
    }

    // Returns null on success, or why nothing was changed.
    public string? Connect(string executable)
    {
        string? command = CommandFor(executable);
        if (command == null) return "Move AI Announce to a folder whose path has no %, \", $ or ` characters, then connect again.";
        string original = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
        var config = JsonNode.Parse(original) as JsonObject ?? throw new JsonException("Claude's settings.json must be an object.");
        var line = config["statusLine"] as JsonObject;
        string existing = line?["command"] is JsonValue value && value.TryGetValue(out string? text) ? text : "";
        if (!string.IsNullOrWhiteSpace(existing) && !existing.Contains(Flag, StringComparison.Ordinal))
            return "Claude Code already runs your own status-line command, so it was left alone. Remove it from ~/.claude/settings.json to use this feed.";
        if (existing == command) return null;
        string backup = settingsPath + ".before-aiannounce";
        if (!File.Exists(backup)) File.WriteAllText(backup, original);
        config["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command };
        WriteAtomic(settingsPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    // Turning the feed off, or uninstalling this copy: put back whatever status line was there before
    // Connect. One set up for another copy of the app stays put.
    public bool Disconnect(string? executable = null)
    {
        try
        {
            if (!File.Exists(settingsPath) || JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject config) return false;
            if ((config["statusLine"] as JsonObject)?["command"] is not JsonValue value || !value.TryGetValue(out string? current)
                || !current.Contains(Flag, StringComparison.Ordinal)) return false;
            if (executable != null && current != CommandFor(executable)) return false;
            JsonNode? before = null;
            string backup = settingsPath + ".before-aiannounce";
            if (File.Exists(backup) && JsonNode.Parse(File.ReadAllText(backup)) is JsonObject original)
                before = original["statusLine"]?.DeepClone();
            if (before != null) config["statusLine"] = before;
            else config.Remove("statusLine");
            WriteAtomic(settingsPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("claude status line: " + ex.Message);
            return false;
        }
    }

    // One status-line run: keep the windows if Claude sent any, and return the text to show.
    internal string Capture(string input, DateTime now)
    {
        using var doc = JsonDocument.Parse(input);
        var limits = Json.Get(doc.RootElement, "rate_limits");
        Window? session = WindowOf(Json.Get(limits, "five_hour")), week = WindowOf(Json.Get(limits, "seven_day"));
        // before a session's first reply there's nothing yet; don't wipe what another session saved
        if (session == null && week == null) return "";
        WriteAtomic(samplePath, JsonSerializer.Serialize(new Sample(now, session, week)));
        string Part(string label, Window? w) => w == null ? "" : $"{label} {w.Percent:0}%";
        return string.Join(" · ", new[] { Part("Session", session), Part("Week", week) }.Where(p => p.Length > 0));
    }

    static Window? WindowOf(JsonElement e)
    {
        if (!UsageParsing.Number(e, "used_percentage", out double percent)) return null;
        long resets = Json.Long(e, "resets_at");
        return new Window(percent, resets > 0 ? DateTimeOffset.FromUnixTimeSeconds(resets).UtcDateTime : null);
    }

    public Sample? Read(DateTime now)
    {
        try
        {
            if (!File.Exists(samplePath) || !Connected) return null;
            var sample = JsonSerializer.Deserialize<Sample>(Json.ReadShared(samplePath));
            return sample == null || sample.At > now.AddMinutes(1) ? null : sample;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static void WriteAtomic(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Claude runs this on every status-line update of every session. Read stdin, save, print one
    // line, exit: no windows, monitors or network.
    public static int Receive(ClaudeUsageFeed feed)
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string payload = input.ReadToEnd();
            if (payload.Length > 2 * 1024 * 1024) return 0;
            string line = feed.Capture(payload, DateTime.UtcNow);
            if (line.Length == 0) return 0;
            using var output = Console.OpenStandardOutput();
            byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
            output.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return 0;
    }
}
