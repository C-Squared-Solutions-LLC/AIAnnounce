using System.Net;
using System.Text;
using AIAnnounce;

static class UsageChecks
{
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    public static async Task Run(Action<bool, string> check)
    {
        DateTime now = DateTime.UtcNow;
        var old = new AgyToken("expired-fixture", now.AddHours(-1));
        var fresh = new AgyToken("current-fixture", now.AddHours(1));
        check(AgyCredentials.Select(new[] { old, fresh }, now) == fresh, "Old credentials cannot hide a current login");
        check(AgyCredentials.Select(new[] { fresh, old }, now) == fresh, "Credential selection is independent of Windows enumeration order");
        string json = """{"token":{"access_token":"test-secret","expiry":"2099-01-01T00:00:00Z"}}""";
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
            check(AgyCredentials.Decode(encoding.GetBytes(json + "\0"))?.AccessToken == "test-secret", $"Reads {encoding.WebName} agy credential");
        check(AgyCredentials.Decode(Encoding.UTF8.GetBytes("not json")) == null, "Unsupported credential content is a diagnostic failure");
        check(AgyCredentials.Decode(Encoding.UTF8.GetBytes("""{"access_token":"flat-token"}"""))?.AccessToken == "flat-token", "Supports older flat OAuth credentials");
        check(!fresh.ToString().Contains(fresh.AccessToken), "Token objects redact their text representation");

        string root = Path.Combine(Path.GetTempPath(), "AIAnnounce-no-project-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new Handler(async request =>
        {
            check(request.RequestUri?.AbsoluteUri == "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary", "Gemini quota request uses agy's Antigravity endpoint");
            check(request.Headers.Authorization?.Parameter == fresh.AccessToken && request.Method == HttpMethod.Post,
                "Quota request uses the selected access token");
            check(await request.Content!.ReadAsStringAsync() == "{}", "An uncached project uses an empty quota request");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"buckets":[{"bucketId":"gemini-weekly","remainingFraction":0.6},{"bucketId":"non-gemini-weekly","remainingFraction":0}]}"""),
            };
        }));
        var source = new AgyUsage(http, () => new(fresh), root);
        var result = await source.ReadAsync();
        check(result.Count == 1 && Math.Abs(result[0].Limits[1].Percent - 40) < .001, "Live quota pipeline returns only Gemini usage");
        string details = UsageDiagnostics.Format(result);
        check(details.Contains("Gemini") && details.Contains("40%") && !details.Contains(fresh.AccessToken), "Usage details contain readable results without tokens");

        using var blockedHttp = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("sensitive-server-content") })));
        var blocked = await new AgyUsage(blockedHttp, () => new(fresh), root).ReadAsync();
        string failure = UsageDiagnostics.Format(blocked);
        check(failure.Contains("HTTP 403") && !failure.Contains("sensitive-server-content"), "HTTP errors expose status without server bodies");
        var unavailable = await new AgyUsage(http, () => new(null, "Credential access failed (error 1312)."), root).ReadAsync();
        check(unavailable[0].Message!.Contains("1312"), "Credential failure keeps the Windows error code");
        var expired = await new AgyUsage(http, () => new(old), root).ReadAsync();
        check(expired[0].Message!.Contains("expired"), "Expired tokens are diagnosed without sending them");
    }
}
