using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIAnnounce;

sealed record UsageLimit(string Label, double Percent, DateTime? ResetsAt, string Severity, bool Available = true, string? Note = null)
{
    public bool Critical => Available && (Severity is "critical" or "exceeded" or "rejected" or "blocked" || Percent >= 90);
    public bool Warning => Available && !Critical && (Severity is "warning" or "allowed_warning" || Percent >= 70);
}

sealed record UsageInfo(IReadOnlyList<UsageLimit> Limits, string? Plan, DateTime FetchedAt, bool Live,
    Cli Provider = Cli.Claude, string? Scope = null, string? Message = null)
{
    public string ProviderName => Provider switch { Cli.Codex => "Codex", Cli.Agy => "Gemini", _ => "Claude" };
    public string DisplayName => Scope == null ? ProviderName : $"{ProviderName} · {Scope}";
    public static UsageInfo Unavailable(Cli provider, string message) => new(new[]
    {
        new UsageLimit("Session", 0, null, "", false, message),
        new UsageLimit("Week", 0, null, "", false, message),
    }, null, DateTime.MinValue, false, provider, Scope: provider == Cli.Agy ? "agy" : null, Message: message);
}

// Plan usage: the numbers behind Claude Code's /usage. Read live from api.anthropic.com/api/oauth/usage
// with the OAuth token Claude Code keeps in .credentials.json, falling back to the copy Claude caches
// in .claude.json. The token goes to that endpoint only, is never logged, and is never refreshed
// here: rotating it would race the Claude sessions that own it. When it's expired we wait for one
// of them to refresh it and show the cached numbers meanwhile.
sealed class ClaudeUsageMonitor : IDisposable
{
    const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    readonly string credentialsPath;
    readonly string configPath;
    readonly string statePath;
    readonly HttpClient http;
    readonly SemaphoreSlim busy = new(1, 1);
    Timer? timer;
    DateTime backoffUntil;
    string? failure;
    sealed record SavedUsage(UsageInfo? Info, DateTime RetryAfter, string? Failure);

    public ClaudeUsageMonitor(string claudeDir) : this(claudeDir, Http,
        Path.Combine(Settings.Dir, "claude-usage-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(claudeDir).ToUpperInvariant())))[..16] + ".json")) { }

    internal ClaudeUsageMonitor(string claudeDir, HttpClient http, string statePath)
    {
        this.http = http;
        this.statePath = statePath;
        credentialsPath = Path.Combine(claudeDir, ".credentials.json");
        // .claude.json lives beside the .claude folder, or inside it when CLAUDE_CONFIG_DIR is set
        string inside = Path.Combine(claudeDir, ".claude.json");
        string beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(claudeDir).TrimEnd('\\', '/')) ?? claudeDir, ".claude.json");
        configPath = File.Exists(inside) ? inside : beside;
        try
        {
            if (File.Exists(statePath) && JsonSerializer.Deserialize<SavedUsage>(Json.ReadShared(statePath)) is { } saved)
            {
                Last = saved.Info?.Provider == Cli.Claude && saved.Info.Limits is { Count: > 0 }
                    ? saved.Info with { Live = false } : null;
                backoffUntil = saved.RetryAfter;
                failure = saved.Failure;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public event Action<UsageInfo>? Updated;

    public UsageInfo? Last { get; private set; }

    public void Start() => timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, Interval);

    public async Task<UsageInfo?> RefreshAsync()
    {
        if (!await busy.WaitAsync(0)) return Last;
        try
        {
            string? plan = ReadCredentials(out string? token, out string? loginFailure);
            UsageInfo? info = null;
            if (DateTime.UtcNow < backoffUntil) failure = BackoffMessage();
            else if (token != null)
                info = await FetchAsync(token, plan);
            else failure = loginFailure;
            if (info == null)
            {
                var cached = ReadCache(plan);
                if (Last is { FetchedAt: var fetched } && fetched != DateTime.MinValue && (cached == null || fetched >= cached.FetchedAt)) cached = Last;
                string message = failure ?? "Claude returned no usage limits.";
                info = cached != null ? cached with { Live = false, Message = message }
                    : UsageInfo.Unavailable(Cli.Claude, message) with { Plan = plan };
            }
            Last = info;
            SaveState();
            Updated?.Invoke(info);
            return info;
        }
        catch (Exception ex)
        {
            Log.Write("usage: refresh failed (" + ex.GetType().Name + ")");
            failure = "Could not refresh Claude usage; displaying the last available data.";
            return Last = Last != null ? Last with { Live = false, Message = failure } : UsageInfo.Unavailable(Cli.Claude, failure);
        }
        finally
        {
            busy.Release();
        }
    }

    async Task<UsageInfo?> FetchAsync(string token, string? plan)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("AIAnnounce/" + AppVersion.Current);
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                backoffUntil = DateTime.UtcNow.AddMinutes(10);
                var requested = response.Headers.RetryAfter?.Date?.UtcDateTime
                    ?? (response.Headers.RetryAfter?.Delta is TimeSpan delay ? DateTime.UtcNow.Add(delay) : DateTime.MinValue);
                if (requested > backoffUntil) backoffUntil = requested;
                failure = BackoffMessage();
                Log.Write("usage: rate limited, backing off 10 min");
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"usage: HTTP {(int)response.StatusCode}");
                failure = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Claude rejected the saved access token (HTTP 401). Open Claude Code to refresh its login."
                    : $"Claude's usage endpoint returned HTTP {(int)response.StatusCode}.";
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var info = Parse(doc.RootElement, plan, DateTime.UtcNow, live: true);
            failure = info == null ? "Claude replied successfully but reported no recognized usage limits." : null;
            if (info != null) backoffUntil = DateTime.MinValue;
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            failure = ex is TaskCanceledException ? "Claude's usage request timed out." : "Could not reach Claude's usage endpoint.";
            Log.Write("usage: " + failure);
            return null;
        }
    }

    // Returns the plan name; hands out the access token only while it's still valid.
    string? ReadCredentials(out string? token, out string? reason)
    {
        token = null;
        reason = "No saved Claude login was found. Sign in to Claude Code to read plan usage.";
        try
        {
            if (!File.Exists(credentialsPath)) return null;
            using var doc = JsonDocument.Parse(Json.ReadShared(credentialsPath));
            var oauth = Json.Get(doc.RootElement, "claudeAiOauth");
            long expires = Json.Long(oauth, "expiresAt");
            if (expires == 0 || Json.FromUnixMs(expires) > DateTime.UtcNow.AddMinutes(1))
            {
                token = Json.Str(oauth, "accessToken");
                reason = token == null ? "Claude's saved login contains no access token." : null;
            }
            else reason = "Claude's saved access token expired. Open Claude Code to refresh it.";
            string? plan = Json.Str(oauth, "subscriptionType");
            return plan == null ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            reason = ex is UnauthorizedAccessException ? "Windows denied access to Claude's login file." : "Could not read Claude's saved login.";
            return null;
        }
    }

    string BackoffMessage() => $"Claude is rate-limiting usage checks (HTTP 429). Retrying after {backoffUntil.ToLocalTime():t}.";

    void SaveState()
    {
        // Preserve only parsed quota data and retry timing, never authentication.
        string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedUsage(Last, backoffUntil, failure)));
            File.Move(temporary, statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    UsageInfo? ReadCache(string? plan)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            using var doc = JsonDocument.Parse(Json.ReadShared(configPath));
            var cached = Json.Get(doc.RootElement, "cachedUsageUtilization");
            var utilization = Json.Get(cached, "utilization");
            return utilization.ValueKind == JsonValueKind.Object
                ? Parse(utilization, plan, Json.FromUnixMs(Json.Long(cached, "fetchedAtMs")), live: false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Newer responses carry limits[] (kind, percent, severity, resets_at); older ones only the
    // five_hour / seven_day windows. Same shape whether it came from the API or Claude's cache.
    public static string? LastRawLimits { get; private set; }  // --usage prints it

    static UsageInfo? Parse(JsonElement u, string? plan, DateTime fetchedAt, bool live)
    {
        var limits = new List<UsageLimit>();
        var list = Json.Get(u, "limits");
        LastRawLimits = list.ValueKind == JsonValueKind.Array ? list.GetRawText() : null;
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (!TryNumber(Json.Get(item, "percent"), out double percent)) continue;
                limits.Add(new UsageLimit(LabelFor(item), percent, ParseTime(Json.Str(item, "resets_at")),
                    Json.Str(item, "severity") ?? ""));
            }
        }

        // The named windows fill in whatever limits[] left out, so a 5-hour window reported only
        // as five_hour still shows up next to limits[]'s weekly and model-scoped entries.
        bool fromList = limits.Count > 0;
        foreach (var (key, label) in new[] { ("five_hour", "Session"), ("seven_day", "Week"), ("seven_day_opus", "Opus wk"), ("seven_day_sonnet", "Sonnet wk") })
        {
            if (fromList && key is not ("five_hour" or "seven_day")) continue;
            if (limits.Any(l => l.Label == label)) continue;
            var window = Json.Get(u, key);
            if (window.ValueKind == JsonValueKind.Object && TryNumber(Json.Get(window, "utilization"), out double percent))
                limits.Add(new UsageLimit(label, percent, ParseTime(Json.Str(window, "resets_at")), ""));
        }
        // Stable: model-scoped limits keep the order Claude sent them in.
        var ordered = limits.OrderBy(l => Rank(l.Label)).ToList();
        return ordered.Count == 0 ? null : new UsageInfo(ordered, plan, fetchedAt, live);
    }

    static int Rank(string label) => label switch { "Session" => 0, "Week" => 1, _ => 2 };

    static string LabelFor(JsonElement item)
    {
        string kind = Json.Str(item, "kind") ?? "";
        string group = Json.Str(item, "group") ?? "";
        bool scoped = Json.Get(item, "scope").ValueKind is JsonValueKind.Object or JsonValueKind.String;
        if (!scoped && (kind is "session" or "five_hour" or "session_all" || group is "session" or "five_hour")) return "Session";
        if (kind == "weekly_all") return "Week";
        // weekly_scoped: "scope": { "model": { "display_name": "Fable" }, "surface": null }
        var scope = Json.Get(item, "scope");
        string? scopeName = scope.ValueKind switch
        {
            JsonValueKind.String => scope.GetString(),
            JsonValueKind.Object => Json.Str(Json.Get(scope, "model"), "display_name")
                                    ?? Json.Str(Json.Get(scope, "surface"), "display_name")
                                    ?? Json.Str(scope, "display_name") ?? Json.Str(scope, "name"),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(scopeName))
            return group switch { "weekly" => $"{scopeName} wk", "session" or "five_hour" => $"{scopeName} 5h", _ => scopeName! };
        return kind.Length == 0 ? "Limit" : char.ToUpperInvariant(kind[0]) + kind[1..].Replace('_', ' ');
    }

    static bool TryNumber(JsonElement e, out double value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value);
    }

    static DateTime? ParseTime(string? s) =>
        s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    public void Dispose() => timer?.Dispose();
}
