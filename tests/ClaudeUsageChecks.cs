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
        using var empty = new ClaudeUsageMonitor(Path.Combine(dir, "no-login"), http, Path.Combine(dir, "no-login-state.json"));
        check((await empty.RefreshAsync())!.Message!.Contains("No saved Claude login"), "Missing credentials retain a distinct diagnostic");
    }
}
