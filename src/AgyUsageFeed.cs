using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIAnnounce;

// Supported agy statusLine input: https://antigravity.google/docs/cli/statusline
// Store only parsed Gemini quotas, never account or conversation metadata.
sealed class AgyUsageFeed
{
    readonly string settingsPath;
    readonly string cachePath;
    string StatusPath => Path.ChangeExtension(cachePath, "status.json");
    sealed record FeedStatus(DateTime At, string State, int Buckets = 0);
    public static string AgyRoot => Path.Combine(UserPaths.Home, ".gemini", "antigravity-cli");
    public AgyUsageFeed() : this(Path.Combine(AgyRoot, "settings.json"), Path.Combine(Settings.Dir, "gemini-usage.json")) { }
    internal AgyUsageFeed(string settingsPath, string cachePath)
    {
        this.settingsPath = settingsPath;
        this.cachePath = cachePath;
    }

    public bool Connected
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Json.ReadShared(settingsPath));
                var line = Json.Get(doc.RootElement, "statusLine");
                return (Json.Str(line, "command")?.Contains("--agy-statusline", StringComparison.Ordinal) ?? false)
                    && Json.Get(line, "enabled").ValueKind != JsonValueKind.False;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }

    public string Connect(string executable)
    {
        string original = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
        var config = JsonNode.Parse(original) as JsonObject ?? throw new JsonException("agy settings must be an object.");
        var line = config["statusLine"] as JsonObject;
        string existing = line?["command"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(existing) && !existing.Contains("--agy-statusline", StringComparison.Ordinal))
            return "agy already has a custom status-line command. It was left unchanged. Remove that command in agy before connecting this usage feed.";

        // cmd expands percent signs even inside quotes. Refuse an ambiguous path.
        executable = Path.GetFullPath(executable);
        if (executable.Contains('%') || executable.Contains('"'))
            return "Move AI Announce to a path without percent signs or quotation marks before connecting Gemini usage.";
        string command = $"\"{executable}\" --agy-statusline";
        if (existing != command || !Connected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            string backup = settingsPath + ".before-aiannounce";
            if (!File.Exists(backup)) File.WriteAllText(backup, original);
            var updated = line?.DeepClone() as JsonObject ?? new JsonObject();
            updated["type"] = "command";
            updated["command"] = command;
            updated["enabled"] = true;
            updated["stack_with_default"] = true;
            config["statusLine"] = updated;
            WriteAtomic(settingsPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return "Gemini usage is connected. To activate it in an already running agy session, paste this command into agy:\r\n\r\n/statusline " + command
            + "\r\n\r\nThen run /usage in agy and Refresh here. Alternatively, reopen agy to load the setting. Your original settings were backed up beside settings.json.";
    }

    // Uninstall: a status line still pointing at the removed exe would run a missing program on every
    // agy state change. Restore the status line from before Connect and keep the rest of agy's settings
    // as they are now. A status line set up for another copy of the app stays put.
    public bool Disconnect(string executable)
    {
        if (!File.Exists(settingsPath)) return false;
        if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject config) return false;
        string ours = $"\"{Path.GetFullPath(executable)}\" --agy-statusline";
        if ((config["statusLine"] as JsonObject)?["command"] is not JsonValue command
            || !command.TryGetValue(out string? current) || current != ours)
            return false;

        JsonNode? before = null;
        string backup = settingsPath + ".before-aiannounce";
        if (File.Exists(backup) && JsonNode.Parse(File.ReadAllText(backup)) is JsonObject original)
            before = original["statusLine"]?.DeepClone();
        if (before != null) config["statusLine"] = before;
        else config.Remove("statusLine");
        WriteAtomic(settingsPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    internal void Capture(string input, DateTime now)
    {
        using var doc = JsonDocument.Parse(input);
        var quota = Json.Get(doc.RootElement, "quota");
        if (quota.ValueKind != JsonValueKind.Object) { WriteStatus(now, "no_quotas"); return; }
        int count = 0;
        foreach (var bucket in quota.EnumerateObject()) count++;
        var results = AgyUsage.Parse(doc.RootElement, now);
        // Startup payloads without quotas must not erase a previous good sample.
        if (results.Count == 0) { WriteStatus(now, count == 0 ? "no_quotas" : "no_gemini_windows", count); return; }
        var info = results[0] with
        {
            Plan = Json.Str(doc.RootElement, "plan_tier"),
            Message = "Gemini model quotas reported by agy's status-line feed.",
        };
        WriteAtomic(cachePath, JsonSerializer.Serialize(info));
        WriteStatus(now, "ready", count);
    }

    public string WaitingMessage()
    {
        try
        {
            var status = JsonSerializer.Deserialize<FeedStatus>(Json.ReadShared(StatusPath));
            if (status != null) return status.State switch
            {
                "no_quotas" => $"agy ran the usage feed at {status.At.ToLocalTime():t}, but has not provided quota data yet. Run /usage in agy.",
                "no_gemini_windows" => $"agy supplied {status.Buckets} quota buckets at {status.At.ToLocalTime():t}, but no recognized Gemini session or weekly limits.",
                "invalid_json" => "agy ran the usage feed, but its input was not valid status-line JSON.",
                "write_failed" => "The agy usage helper ran but could not save its quota data.",
                _ => "The Gemini usage feed ran, but its saved quota data could not be read.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return "agy has not run the Gemini usage feed yet. Use Connect Gemini usage for the /statusline activation command, or reopen agy itself.";
    }

    void WriteStatus(DateTime now, string state, int count = 0) =>
        WriteAtomic(StatusPath, JsonSerializer.Serialize(new FeedStatus(now, state, count)));

    public IReadOnlyList<UsageInfo>? Read(DateTime now)
    {
        if (!Connected) return null;
        try
        {
            var info = JsonSerializer.Deserialize<UsageInfo>(Json.ReadShared(cachePath));
            if (info == null || info.Provider != Cli.Agy || info.FetchedAt == DateTime.MinValue
                || info.FetchedAt > now.AddMinutes(1) || info.Limits is not { Count: > 0 }) return null;
            bool recent = now - info.FetchedAt <= TimeSpan.FromMinutes(3);
            return new[] { info with { Live = recent, Message = recent
                ? "Gemini model quotas reported by agy's status-line feed."
                : "Last Gemini quotas reported by agy. Run /usage in agy to refresh them." } };
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

    public static int Receive(AgyUsageFeed? feed = null)
    {
        feed ??= new AgyUsageFeed();
        try
        {
            // agy invokes this on state changes with a five-second timeout.
            // Do not initialize monitors, windows or networking in this helper.
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            var buffer = new char[4096];
            var payload = new StringBuilder();
            while (payload.Length <= 2 * 1024 * 1024)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                payload.Append(buffer, 0, read);
            }
            if (payload.Length <= 2 * 1024 * 1024) feed.Capture(payload.ToString(), DateTime.UtcNow);
            // Empty stdout adds no custom line; stack_with_default keeps agy's UI.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try { feed.WriteStatus(DateTime.UtcNow, ex is JsonException ? "invalid_json" : "write_failed"); }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
        }
        return 0;
    }
}
