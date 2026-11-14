using System.Net;
using System.Text.Json;
using AIAnnounce;

static class ClaudeUsageChecks
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

    sealed class LoginHandler(Func<string?, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(response(request.Headers.Authorization?.Parameter));
        }
    }

    // /login to another account while the old one is rate-limited.
    static async Task SwitchingAccounts(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIAnnounce-switch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string state = Path.Combine(dir, "state.json");
        void SignIn(string token, string account) => File.WriteAllText(Path.Combine(dir, ".credentials.json"),
            "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"subscriptionType\":\"max\"}}");
        // Claude Code's cache still holds account A's numbers whoever is signed in
        const string cache = "\"cachedUsageUtilization\":{\"accountUuid\":\"acct-a\",\"fetchedAtMs\":1794433086700," +
            "\"utilization\":{\"seven_day\":{\"utilization\":50,\"resets_at\":\"2099-11-18T12:00:00Z\"}}}";
        void Account(string account) => File.WriteAllText(Path.Combine(dir, ".claude.json"),
            "{\"oauthAccount\":{\"accountUuid\":\"" + account + "\"}," + cache + "}");
        SignIn("login-a", "acct-a");
        Account("acct-a");
        var handler = new LoginHandler(token => token == "login-b"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"seven_day":{"utilization":12,"resets_at":"2099-11-18T12:00:00Z"}}""") }
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);
        using (var monitor = new ClaudeUsageMonitor(dir, http, state))
        {
            var limited = (await monitor.RefreshAsync())!;
            check(!limited.Live && limited.Limits.Single(l => l.Available).Percent == 50 && limited.Message!.Contains("429"),
                "A rate-limited login shows Claude's cached numbers for that same account");
            await monitor.RefreshAsync();
            check(handler.Requests == 1 && !monitor.LoginChanged(), "The same login sits out its back-off");

            SignIn("login-b", "acct-b");
            Account("acct-b");
            check(monitor.LoginChanged(), "A /login is noticed without a request");
            var switched = (await monitor.RefreshAsync())!;
            check(handler.Requests == 2 && switched.Live && switched.Limits.Single().Percent == 12,
                "A new login is checked right away instead of waiting out the old one's 429");

            SignIn("login-c", "acct-c");
            Account("acct-c");
            var third = (await monitor.RefreshAsync())!;
            check(third.Limits.All(l => !l.Available) && third.Message!.Contains("429"),
                "After a switch, neither the last account's numbers nor another account's cache stand in for this one");
        }
        using (var reopened = new ClaudeUsageMonitor(dir, http, state))
        {
            await reopened.RefreshAsync();
            check(handler.Requests == 3, "The back-off for the current login survives a restart");
            SignIn("login-b", "acct-b");
            Account("acct-b");
            check((await reopened.RefreshAsync())!.Live && handler.Requests == 4, "Switching back after a restart is checked right away too");
        }
        string saved = File.ReadAllText(state);
        check(!saved.Contains("login-a") && !saved.Contains("login-b") && !saved.Contains("login-c"), "Saved state holds login fingerprints, not tokens");
    }

    public static async Task Run(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIAnnounce-claude-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string state = Path.Combine(dir, "saved-usage.json");
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"fixture-secret","subscriptionType":"max"}}""");
        bool rateLimited = false;
        var handler = new Handler(() => rateLimited ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"seven_day":{"utilization":74,"resets_at":"2099-11-04T12:00:00Z"}}""") });
        using var http = new HttpClient(handler);
        using (var monitor = new ClaudeUsageMonitor(dir, http, state))
        {
            var live = (await monitor.RefreshAsync())!;
            check(live.Live && live.Limits.Single().Percent == 74, "Claude live usage is parsed and saved");
            rateLimited = true;
            var limited = (await monitor.RefreshAsync())!;
            check(!limited.Live && limited.Limits.Single().Percent == 74 && limited.Message!.Contains("429"),
                "Claude 429 preserves last usage and reports rate limiting");
            check(!limited.Message!.Contains("Sign in"), "Rate limiting never becomes a sign-in failure");
            await monitor.RefreshAsync();
            check(handler.Requests == 2, "Refresh respects Claude's cooldown");
        }
        using (var reopened = new ClaudeUsageMonitor(dir, http, state))
        {
            var cached = (await reopened.RefreshAsync())!;
            check(cached.Limits.Single().Percent == 74 && cached.Message!.Contains("429") && !cached.Live,
                "Cached Claude usage and the cooldown survive overlay restart");
            check(handler.Requests == 2, "Reopening the overlay does not retry during the provider cooldown");
        }
        check(!File.ReadAllText(state).Contains("fixture-secret"), "Claude's persisted state contains no access token");

        using (var firstFailure = new ClaudeUsageMonitor(dir, http, Path.Combine(dir, "first-failure.json")))
        {
            var missing = (await firstFailure.RefreshAsync())!;
            check(missing.Limits.All(l => !l.Available) && missing.Message!.Contains("429"),
                "A rate-limited first launch reports the actual cause even without a cache");
        }
        // Make the cooldown expire without waiting ten minutes.
        File.WriteAllText(state, JsonSerializer.Serialize(new { Info = (UsageInfo?)null, RetryAfter = DateTime.UtcNow.AddMinutes(-1), Failure = "old" }));
        rateLimited = false;
        using (var recovered = new ClaudeUsageMonitor(dir, http, state))
            check((await recovered.RefreshAsync())!.Live, "Claude retries successfully after the saved cooldown expires");
        // Shape of a 2026 plan response: a weekly limit plus a Fable-scoped weekly limit, no session window.
        var scopedHandler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
            {"five_hour":null,"seven_day":{"utilization":3,"resets_at":"2099-11-18T12:59:59Z"},"limits":[
              {"kind":"weekly_all","group":"weekly","percent":3,"severity":"normal","resets_at":"2099-11-18T12:59:59Z","scope":null},
              {"kind":"weekly_scoped","group":"weekly","percent":4,"severity":"normal","resets_at":"2099-11-18T12:59:59Z",
               "scope":{"model":{"id":null,"display_name":"Fable"},"surface":null}}]}
            """) });
        using var scopedHttp = new HttpClient(scopedHandler);
        using (var scoped = new ClaudeUsageMonitor(dir, scopedHttp, Path.Combine(dir, "scoped.json")))
        {
            var info = (await scoped.RefreshAsync())!;
            var cells = UsageParsing.Shown(info.Limits, "No data reported.");
            check(cells.Select(c => c.Label).SequenceEqual(new[] { "Week", "Fable wk" }) && cells[1].Percent == 4,
                "The Fable weekly limit takes the cell of a session window the plan doesn't report");
        }
        async Task<IReadOnlyList<string>> Cells(string body)
        {
            using var h = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
            using var m = new ClaudeUsageMonitor(dir, h, Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"));
            var info = (await m.RefreshAsync())!;
            return UsageParsing.Shown(info.Limits, "").Select(c => c.Available ? $"{c.Label} {c.Percent:0}" : $"{c.Label} -").ToList();
        }
        const string fiveHour = "\"five_hour\":{\"utilization\":55,\"resets_at\":\"2099-11-11T18:00:00Z\"}";
        const string week = """{"kind":"weekly_all","group":"weekly","percent":30,"resets_at":"2099-11-18T12:59:59Z","scope":null}""";
        const string fable = """{"kind":"weekly_scoped","group":"weekly","percent":20,"resets_at":"2099-11-18T12:59:59Z","scope":{"model":{"display_name":"Fable"},"surface":null}}""";
        const string session = """{"kind":"session","group":"session","percent":55,"resets_at":"2099-11-11T18:00:00Z","scope":null}""";
        var listed = await Cells("{" + fiveHour + ",\"limits\":[" + week + "," + fable + "," + session + "]}");
        check(listed.SequenceEqual(new[] { "Session 55", "Week 30", "Fable wk 20" }),
            "A 5-hour plan shows session, week and Fable, session first wherever limits[] lists it");
        var named = await Cells("{" + fiveHour + ",\"limits\":[" + week + "," + fable + "]}");
        check(named.SequenceEqual(new[] { "Session 55", "Week 30", "Fable wk 20" }),
            "A 5-hour window reported only as five_hour still joins limits[]'s weekly entries");

        var withSession = UsageParsing.Shown(new[] {
            new UsageLimit("Session", 40, null, ""), new UsageLimit("Week", 70, null, ""),
            new UsageLimit("Fable wk", 31, null, ""), new UsageLimit("Opus wk", 12, null, "") }, "");
        check(withSession.Select(c => c.Label).SequenceEqual(new[] { "Session", "Week", "Fable wk" }),
            "Session, week and the first model limit share the row; the rest stay in the tooltip");
        check(UsageParsing.Shown(new[] { new UsageLimit("Week", 70, null, "") }, "").Select(c => c.Label).SequenceEqual(new[] { "Session", "Week" }),
            "Without a model limit the row still shows both windows");

        await SwitchingAccounts(check);

        using var empty = new ClaudeUsageMonitor(Path.Combine(dir, "no-login"), http, Path.Combine(dir, "no-login-state.json"));
        check((await empty.RefreshAsync())!.Message!.Contains("No saved Claude login"), "Missing credentials retain a distinct diagnostic");
    }
}
