using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIAnnounce;

// Providers fail independently. Local Codex updates are cheap; authenticated reads are throttled.
sealed class UsageMonitor : IDisposable
{
    readonly ClaudeUsageMonitor claude;
    readonly CodexUsage codex = new();
    readonly AgyUsage agy = new();
    readonly SemaphoreSlim gate = new(1, 1);
    Timer? timer;
    DateTime nextRemote;
    bool disposed;
    public IReadOnlyList<UsageInfo> Last { get; private set; } = Array.Empty<UsageInfo>();
    public event Action<IReadOnlyList<UsageInfo>>? Updated;

    public UsageMonitor(string claudeDir) => claude = new(claudeDir);

    public void Start()
    {
        timer?.Dispose();
        timer = new Timer(_ => _ = RefreshAsync(false), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    // Plan usage switched off: no more credential reads or quota requests until Start.
    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        Last = Array.Empty<UsageInfo>();
    }

    public async Task<IReadOnlyList<UsageInfo>> RefreshAsync(bool force = true)
    {
        if (disposed) return Last;
        if (force) await gate.WaitAsync();
        else if (!await gate.WaitAsync(0)) return Last;
        try
        {
            if (disposed) return Last;
            bool remote = force || DateTime.UtcNow >= nextRemote;
            if (remote) nextRemote = DateTime.UtcNow.AddMinutes(3);
            var c = remote ? claude.RefreshAsync() : Task.FromResult(claude.Last);
            var x = Task.Run(() => codex.ReadAsync(remote));
            var a = agy.ReadAsync(remote);
            await Task.WhenAll(c, x, a);
            var result = new List<UsageInfo> { c.Result ?? UsageInfo.Unavailable(Cli.Claude, "Sign in to Claude Code to read plan usage.") };
            result.Add(x.Result);
            result.AddRange(a.Result);
            Last = result;
            if (!disposed) Updated?.Invoke(Last);
            return Last;
        }
        finally { gate.Release(); }
    }

    public void Dispose() { disposed = true; timer?.Dispose(); claude.Dispose(); }
}

static class UsageParsing
{
    public static bool Number(JsonElement e, string name, out double n)
    {
        var v = Json.Get(e, name);
        n = 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out n) && double.IsFinite(n);
    }

    public static DateTime? Time(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return at;
        return null;
    }

    public static IReadOnlyList<UsageLimit> Windows(IEnumerable<UsageLimit> limits, string missing)
    {
        var all = limits.ToList();
        return new[] { "Session", "Week" }.Select(label => all.FirstOrDefault(l => l.Label == label)
            ?? new UsageLimit(label, 0, null, "", false, missing)).Concat(all.Where(l => l.Label is not ("Session" or "Week"))).ToList();
    }

    public static UsageInfo Cached(UsageInfo info, string message) => info with { Live = false, Message = message };

    // No redirects: bearer tokens are sent only to the intended provider host.
    public static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
}

sealed class CodexUsage
{
    readonly string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir
        ? dir : Path.Combine(UserPaths.Home, ".codex");
    readonly Dictionary<string, (long Size, DateTime Written, UsageInfo? Info)> files = new();
    UsageInfo? last;
    DateTime backoff;

    public async Task<UsageInfo> ReadAsync(bool remote)
    {
        if (remote && DateTime.UtcNow >= backoff)
        {
            try { if (await FetchAsync() is UsageInfo live) last = live; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or TaskCanceledException)
            { if (last != null) last = UsageParsing.Cached(last, "Last reported limits; refresh unavailable."); }
        }
        try
        {
            var cached = ReadRollouts();
            if (cached != null && (last == null || cached.FetchedAt > last.FetchedAt)) last = cached;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or TaskCanceledException)
        {
            if (last != null) last = UsageParsing.Cached(last, "Last reported limits; refresh unavailable.");
        }
        return last ?? UsageInfo.Unavailable(Cli.Codex, "No limits reported yet. Sign in to Codex and run a turn.");
    }

    async Task<UsageInfo?> FetchAsync()
    {
        string path = Path.Combine(home, "auth.json");
        if (!File.Exists(path)) return null;
        using var auth = JsonDocument.Parse(Json.ReadShared(path));
        var tokens = Json.Get(auth.RootElement, "tokens");
        if (Json.Str(tokens, "access_token") is not string token) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (Json.Str(tokens, "account_id") is string account) request.Headers.Add("ChatGPT-Account-Id", account);
        using var response = await UsageParsing.Http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) backoff = DateTime.UtcNow.AddMinutes(10);
        if (!response.IsSuccessStatusCode)
        {
            if (last != null) last = UsageParsing.Cached(last, $"Last reported limits (refresh HTTP {(int)response.StatusCode}).");
            return null;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return Parse(doc.RootElement, DateTime.UtcNow, true);
    }

    UsageInfo? ReadRollouts()
    {
        string root = Path.Combine(home, "sessions");
        if (!Directory.Exists(root)) return null;
        var recent = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).Take(24).ToList();
        foreach (string gone in files.Keys.Except(recent.Select(f => f.FullName)).ToList()) files.Remove(gone);
        foreach (var f in recent)
        {
            if (files.TryGetValue(f.FullName, out var old) && old.Size == f.Length && old.Written == f.LastWriteTimeUtc) continue;
            UsageInfo? found = null;
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Position = Math.Max(0, fs.Length - (2 << 20));
                using var reader = new StreamReader(fs);
                if (fs.Position > 0) reader.ReadLine(); // discard a partial first record
                while (reader.ReadLine() is string line)
                {
                    if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var e = doc.RootElement;
                        var payload = Json.Get(e, "payload");
                        if (Json.Str(e, "type") != "event_msg" || Json.Str(payload, "type") != "token_count") continue;
                        var value = Parse(Json.Get(payload, "rate_limits"), Json.Timestamp(e), false);
                        if (value != null && (found == null || value.FetchedAt >= found.FetchedAt)) found = value;
                    }
                    catch (JsonException) { } // concurrent append
                }
                files[f.FullName] = (f.Length, f.LastWriteTimeUtc, found ?? old.Info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files.Values.Select(f => f.Info).Where(i => i != null).OrderByDescending(i => i!.FetchedAt).FirstOrDefault();
    }

    // Both the rollout's snake_case and app-server's camelCase are supported. Window length,
    // not primary/secondary position, distinguishes weekly-only plans from five-hour plans.
    public static UsageInfo? Parse(JsonElement root, DateTime at, bool live)
    {
        var limits = new List<UsageLimit>();
        string? plan = Json.Str(root, "plan_type") ?? Json.Str(root, "planType");
        var rate = Json.Get(root, "rate_limit");
        if (rate.ValueKind != JsonValueKind.Object) rate = root;
        string? id = Json.Str(rate, "limit_id") ?? Json.Str(rate, "limitId");
        if (id != null && id != "codex") return null;
        foreach (string key in new[] { "primary", "secondary", "primary_window", "secondary_window" })
        {
            var w = Json.Get(rate, key);
            if (!UsageParsing.Number(w, "used_percent", out double used) && !UsageParsing.Number(w, "usedPercent", out used)) continue;
            double minutes = Json.Long(w, "window_minutes");
            if (minutes == 0) minutes = Json.Long(w, "windowDurationMins");
            if (minutes == 0) minutes = Json.Long(w, "limit_window_seconds") / 60.0;
            if (minutes <= 0) continue; // do not guess which window this is
            string label = minutes == 7 * 24 * 60 ? "Week" : minutes <= 5 * 60 ? "Session" : $"{minutes / 60:0.#}h";
            var reset = UsageParsing.Time(Json.Get(w, "resets_at")) ?? UsageParsing.Time(Json.Get(w, "resetsAt")) ?? UsageParsing.Time(Json.Get(w, "reset_at"));
            limits.Add(new(label, Math.Clamp(used, 0, 100), reset, ""));
        }
        if (limits.Count == 0) return null;
        return new(UsageParsing.Windows(limits, "This window is not reported by the current Codex plan."), plan, at, live, Cli.Codex,
            Message: live ? "From Codex account usage." : "Last reported by Codex; updates as sessions run.");
    }
}

sealed class AgyUsage
{
    // Antigravity CLI's quota service. The similarly named cloudcode-pa host
    // serves a different product route and rejects these requests with HTTP 403.
    internal const string Endpoint = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    readonly string root;
    readonly HttpClient http;
    readonly Func<AgyCredentialResult> readCredentials;
    readonly AgyUsageFeed feed;
    DateTime backoff;
    public IReadOnlyList<UsageInfo> Last { get; private set; } = new[] { UsageInfo.Unavailable(Cli.Agy, "Waiting for Gemini quota data from agy.") };

    public AgyUsage() : this(UsageParsing.Http, AgyCredentials.Read, AgyUsageFeed.AgyRoot, new AgyUsageFeed()) { }
    internal AgyUsage(HttpClient http, Func<AgyCredentialResult> readCredentials, string root, AgyUsageFeed? feed = null)
    {
        this.http = http;
        this.readCredentials = readCredentials;
        this.root = root;
        this.feed = feed ?? new AgyUsageFeed(Path.Combine(root, "settings.json"), Path.Combine(root, "feed.json"));
    }

    public async Task<IReadOnlyList<UsageInfo>> ReadAsync(bool remote = true)
    {
        try
        {
            if (feed.Read(DateTime.UtcNow) is { } reported) return Last = reported;
            if (feed.Connected) return Unavailable(feed.WaitingMessage());
            if (!remote) return Last;
            if (DateTime.UtcNow < backoff) return Last;
            var credentials = readCredentials();
            if (credentials.Token == null) return Unavailable(credentials.Error ?? "agy has no saved access token.");
            if (credentials.Token.ExpiresAt <= DateTime.UtcNow)
                return Unavailable($"agy's saved access token expired at {credentials.Token.ExpiresAt.Value.ToLocalTime():g}. Run /usage in agy, then refresh here.");
            string projectPath = Path.Combine(root, "cache", "default_project_id.txt");
            string? project = File.Exists(projectPath) ? File.ReadAllText(projectPath).Trim() : null;
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token.AccessToken);
            request.Content = new StringContent(project == null ? "{}" : JsonSerializer.Serialize(new { project }), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) backoff = DateTime.UtcNow.AddMinutes(10);
            if (!response.IsSuccessStatusCode) return Unavailable(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Google rejected agy's saved token (HTTP 401). Run /usage in agy, then refresh here.",
                HttpStatusCode.Forbidden => "Google denied access to agy's quota endpoint (HTTP 403). Use Connect Gemini usage to read agy's own quota feed.",
                HttpStatusCode.TooManyRequests => "Google rate-limited the quota check (HTTP 429). Retrying in 10 minutes.",
                _ => $"Google's quota endpoint returned HTTP {(int)response.StatusCode}.",
            });
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var parsed = Parse(doc.RootElement, DateTime.UtcNow);
            if (parsed.Count == 0) return Unavailable("Google replied successfully, but returned no recognized Gemini session or weekly quota buckets.");
            return Last = parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or TaskCanceledException)
        {
            return Unavailable(ex switch
            {
                TaskCanceledException => "Google's quota request timed out after 10 seconds.",
                HttpRequestException requestError => $"Could not reach Google's quota endpoint ({requestError.HttpRequestError}).",
                JsonException => "Google's quota response was not valid JSON.",
                UnauthorizedAccessException => "Windows denied access to agy's local project settings.",
                _ => "Could not read agy's local project settings.",
            });
        }
    }

    IReadOnlyList<UsageInfo> Unavailable(string why)
    {
        Last = Last.Any(i => i.Limits.Any(l => l.Available))
            ? Last.Select(i => UsageParsing.Cached(i, why)).ToList()
            : new[] { UsageInfo.Unavailable(Cli.Agy, why) };
        return Last;
    }

    public static IReadOnlyList<UsageInfo> Parse(JsonElement root, DateTime at)
    {
        var limits = new List<UsageLimit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Some accounts return a "non-gemini" family. A substring match on "gemini"
        // would incorrectly include it, so explicitly reject other/shared model families.
        static bool OtherModels(string text) => Regex.IsMatch(text, @"non[\s_-]*gemini|claude|gpt|all[\s_-]*models", RegexOptions.IgnoreCase);
        static bool Gemini(string text) => !OtherModels(text) && Regex.IsMatch(text, @"(?<![a-z0-9])gemini(?![a-z0-9])", RegexOptions.IgnoreCase);
        void Bucket(JsonElement bucket, string? mapKey = null, string groupName = "")
        {
            string id = Json.Str(bucket, "bucketId") ?? Json.Str(bucket, "bucket_id") ?? mapKey ?? "";
            string display = Json.Str(bucket, "displayName") ?? Json.Str(bucket, "display_name") ?? "";
            string window = Json.Str(bucket, "window") ?? "";
            string description = $"{id} {display} {window}".ToLowerInvariant();
            string family = $"{id} {display}";
            if (OtherModels(family) || (!Gemini(family) && !Gemini(groupName))) return;
            string? label = description.Contains("week") || description.Contains("7-day") ? "Week"
                : description.Contains("session") || description.Contains("5-hour") || description.Contains("five_hour") || description.Contains("5h") || description.Contains("five-hour") || description.Contains("5 hour") || description.Contains("five hour") ? "Session" : null;
            if (label == null || !seen.Add(id.Length > 0 ? id : description)) return;
            if (Json.Bool(bucket, "disabled")) { limits.Add(new(label, 0, null, "", false, "This quota bucket is disabled.")); return; }
            bool available = UsageParsing.Number(bucket, "remainingFraction", out double fraction) || UsageParsing.Number(bucket, "remaining_fraction", out fraction);
            var reset = UsageParsing.Time(Json.Get(bucket, "resetTime")) ?? UsageParsing.Time(Json.Get(bucket, "reset_time"));
            limits.Add(new(label, available ? Math.Clamp((1 - fraction) * 100, 0, 100) : 0, reset, "", available,
                available ? null : "agy reports an amount without a percentage for this Gemini bucket."));
        }
        void List(JsonElement list, string groupName = "")
        {
            if (list.ValueKind == JsonValueKind.Array) foreach (var b in list.EnumerateArray()) Bucket(b, groupName: groupName);
        }
        List(Json.Get(root, "buckets"));
        var groups = Json.Get(root, "groups");
        if (groups.ValueKind == JsonValueKind.Array)
            foreach (var group in groups.EnumerateArray())
                List(Json.Get(group, "buckets"), $"{Json.Str(group, "displayName") ?? Json.Str(group, "display_name")} {Json.Str(group, "groupId") ?? Json.Str(group, "group_id")} {Json.Str(group, "name")}");
        // Also accept the official statusLine JSON format, useful for diagnostics/fixtures.
        var quota = Json.Get(root, "quota");
        if (quota.ValueKind == JsonValueKind.Object) foreach (var b in quota.EnumerateObject()) Bucket(b.Value, b.Name);
        if (limits.Count == 0) return Array.Empty<UsageInfo>();
        return new[] { new UsageInfo(UsageParsing.Windows(limits, "This Gemini window is not reported by agy."), null, at, true,
            Cli.Agy, "agy", "Gemini model quotas from agy; percentages show quota used.") };
    }
}

sealed record AgyToken(string AccessToken, DateTime? ExpiresAt)
{
    public override string ToString() => "agy token (redacted)";
}
sealed record AgyCredentialResult(AgyToken? Token, string? Error = null);

// agy owns these OAuth credentials. Read only; never rotate, persist or log its tokens.
static class AgyCredentials
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string? Target, Comment;
        public long Written;
        public uint Size;
        public IntPtr Blob;
        public uint Persist, Count;
        public IntPtr Attributes;
        public string? Alias, User;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentials);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);

    public static AgyCredentialResult Read()
    {
        if (!CredEnumerateW(null, 0, out uint count, out IntPtr entries))
        {
            int error = Marshal.GetLastWin32Error();
            return new(null, error switch
            {
                1312 => "Windows Credential Manager is unavailable in this logon session (error 1312). Run AI Announce from your normal signed-in desktop.",
                1168 => "Windows Credential Manager contains no saved credentials for this Windows account. Sign in to agy first.",
                5 => "Windows denied access to Credential Manager (error 5).",
                _ => $"Windows Credential Manager could not be read (error {error}).",
            });
        }
        int matched = 0;
        var tokens = new List<AgyToken>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var c = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(entries, i * IntPtr.Size));
                string name = (c.Target ?? "").ToLowerInvariant();
                if (!new[] { "antigravity", "gemini", "jetski", "codeassist", "cloud-code", "cloudcode" }.Any(name.Contains)) continue;
                matched++;
                if (c.Type != 1 || c.Size == 0 || c.Size > 65536) continue;
                var data = new byte[c.Size];
                Marshal.Copy(c.Blob, data, 0, data.Length);
                try { if (Decode(data) is AgyToken token) tokens.Add(token); }
                finally { Array.Clear(data); }
            }
        }
        finally { CredFree(entries); }
        return new(Select(tokens, DateTime.UtcNow), tokens.Count > 0 ? null : matched == 0
            ? "Credential Manager is accessible, but no agy/Gemini sign-in was found. Sign in to agy with this Windows account."
            : "An agy/Gemini credential was found, but its format contains no readable OAuth access token.");
    }

    internal static AgyToken? Decode(byte[] data)
    {
        // go-keyring stores UTF-8; some older Windows clients stored UTF-16.
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            try
            {
                using var doc = JsonDocument.Parse(encoding.GetString(data).TrimEnd('\0').TrimStart('\uFEFF'));
                var value = Json.Get(doc.RootElement, "token");
                if (value.ValueKind != JsonValueKind.Object) value = doc.RootElement;
                if (Json.Str(value, "access_token") is { Length: > 0 } token)
                    return new(token, UsageParsing.Time(Json.Get(value, "expiry")));
            }
            catch (JsonException) { }
        }
        return null;
    }

    // Enumeration order is unspecified. An expired credential must not hide a newer login.
    internal static AgyToken? Select(IEnumerable<AgyToken> tokens, DateTime now) => tokens
        .OrderByDescending(t => t.ExpiresAt == null || t.ExpiresAt > now)
        .ThenByDescending(t => t.ExpiresAt ?? DateTime.MinValue).FirstOrDefault();
}
