using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIAnnounce;

static class ClaudeFeedChecks
{
    sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(response());
        }
    }

    public static async Task Run(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIAnnounce-claude-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string settings = Path.Combine(dir, "settings.json");
        string sample = Path.Combine(dir, "sample.json");
        const string original = """{"model":"fable","permissions":{"allow":["Bash(git status)"]},"enabledPlugins":{"x@y":true}}""";
        File.WriteAllText(settings, original);
        var feed = new ClaudeUsageFeed(settings, sample);
        const string exe = @"C:\Program Files\AI Announce\AIAnnounce.exe";

        check(!feed.Connected && feed.Read(DateTime.UtcNow) == null, "An unconnected feed supplies nothing");
        check(feed.Connect(exe) == null && feed.Connected, "Connecting points Claude's status line at the app");
        using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
        {
            var root = doc.RootElement;
            check(root.GetProperty("statusLine").GetProperty("command").GetString() == "\"C:/Program Files/AI Announce/AIAnnounce.exe\" --claude-statusline"
                  && root.GetProperty("statusLine").GetProperty("type").GetString() == "command",
                "The command is quoted with forward slashes, so Git Bash and cmd both run it");
            check(root.GetProperty("model").GetString() == "fable" && root.GetProperty("permissions").GetProperty("allow")[0].GetString() == "Bash(git status)"
                  && root.GetProperty("enabledPlugins").GetProperty("x@y").GetBoolean(), "Every other Claude setting is kept");
        }
        check(File.ReadAllText(settings + ".before-aiannounce") == original, "Claude's settings are backed up before the first change");
        check(ClaudeUsageFeed.CommandFor(@"C:\100%\AIAnnounce.exe") == null && ClaudeUsageFeed.CommandFor(@"C:\$HOME\AIAnnounce.exe") == null,
            "A path a shell would expand is refused");

        DateTime now = new(2026, 11, 13, 21, 0, 0, DateTimeKind.Utc);
        long Epoch(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
        string Limits(double session, DateTime sessionReset, double week, DateTime weekReset) =>
            "\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + session.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"resets_at\":" + Epoch(sessionReset) + "},\"seven_day\":{\"used_percentage\":" + week.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"resets_at\":" + Epoch(weekReset) + "}}";
        string line = feed.Capture("{\"session_id\":\"s1\",\"model\":{\"display_name\":\"Fable 5.2\"}," +
            Limits(41.5, now.AddHours(2), 12, now.AddDays(4)) + "}", now);
        var read = feed.Read(now);
        check(line == "Session 42% · Week 12%" && read?.Session?.Percent == 41.5 && read.Week?.ResetsAt == now.AddDays(4),
            "A status-line run saves both windows and shows them");
        check(!File.ReadAllText(sample).Contains("s1") && !File.ReadAllText(sample).Contains("Fable"), "Only the windows are saved, nothing about the session");
        check(feed.Capture("""{"session_id":"s2","model":{"display_name":"Fable 5.2"}}""", now.AddMinutes(1)) == ""
              && feed.Read(now)?.Session?.Percent == 41.5, "A session with no reply yet leaves another session's numbers alone");

        // The endpoint is rate-limited; the status line keeps Session and Week live, Fable stays as cached.
        string claudeDir = Path.Combine(dir, "claude");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"feed-token","subscriptionType":"team"}}""");
        string state = Path.Combine(dir, "state.json");
        File.WriteAllText(state, JsonSerializer.Serialize(new
        {
            Info = new UsageInfo(new[]
            {
                new UsageLimit("Session", 10, DateTime.UtcNow.AddHours(1), ""), new UsageLimit("Week", 3, DateTime.UtcNow.AddDays(4), ""),
                new UsageLimit("Fable wk", 7, DateTime.UtcNow.AddDays(4), ""),
            }, "Team", DateTime.UtcNow.AddHours(-1), true),
            RetryAfter = DateTime.UtcNow.AddHours(1), Failure = "429", BackoffLogin = (string?)null,
        }));
        var liveFeed = new ClaudeUsageFeed(settings, Path.Combine(dir, "live-sample.json"));
        liveFeed.Capture("{" + Limits(55, DateTime.UtcNow.AddHours(2), 20, DateTime.UtcNow.AddDays(4)) + "}", DateTime.UtcNow);
        var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);
        using (var monitor = new ClaudeUsageMonitor(claudeDir, http, state, liveFeed))
        {
            var info = (await monitor.RefreshAsync())!;
            string Cell(string label) => info.Limits.FirstOrDefault(l => l.Label == label) is { } l ? $"{l.Percent:0}" : "-";
            check(handler.Requests == 0 && info.Live && Cell("Session") == "55" && Cell("Week") == "20" && Cell("Fable wk") == "7",
                "While the endpoint is backed off, Session and Week come live from the status line and Fable stays cached");
            check(info.Limits[0].Label == "Session" && info.Limits[1].Label == "Week", "Status-line windows keep their places in the row");
            check(monitor.FeedLive, "A fresh status-line sample counts as live, so the endpoint is asked less often");
        }

        check(feed.Disconnect(@"D:\other\AIAnnounce.exe") == false && feed.Connected, "Another copy's uninstall leaves the feed alone");
        check(feed.Disconnect(exe) && !feed.Connected && JsonNode.Parse(File.ReadAllText(settings))!["statusLine"] == null
              && JsonNode.Parse(File.ReadAllText(settings))!["model"]!.GetValue<string>() == "fable",
            "Disconnecting removes only the status line, since there wasn't one before");

        File.WriteAllText(settings, """{"statusLine":{"type":"command","command":"~/.claude/my-line.sh"}}""");
        check(feed.Connect(exe) != null && File.ReadAllText(settings).Contains("my-line.sh"), "Your own status line is never replaced");
    }
}
