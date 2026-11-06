using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIAnnounce;

static class AgyFeedChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIAnnounce-feed-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string settings = Path.Combine(dir, "settings.json");
        string cache = Path.Combine(dir, "quota.json");
        string original = """{"model":"Gemini 4 Argon (High)","statusLine":{"command":"","enabled":true,"padding":2},"trustedWorkspaces":["C:\\test"]}""";
        File.WriteAllText(settings, original);
        var feed = new AgyUsageFeed(settings, cache);
        check(!feed.Connected && feed.Read(DateTime.UtcNow) == null, "Unconnected feed never supplies old quota");
        check(feed.WaitingMessage().Contains("has not run"), "An unstarted feed has a distinct diagnostic");
        feed.Connect(@"C:\Program Files\AI Announce\AIAnnounce.exe");
        using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
        {
            var line = doc.RootElement.GetProperty("statusLine");
            check(feed.Connected && line.GetProperty("stack_with_default").GetBoolean(), "Feed connection keeps agy's built-in status line");
            check(line.GetProperty("padding").GetInt32() == 2 && doc.RootElement.GetProperty("model").GetString() == "Gemini 4 Argon (High)", "Connection preserves model and other settings");
            check(line.GetProperty("command").GetString() == "\"C:\\Program Files\\AI Announce\\AIAnnounce.exe\" --agy-statusline", "Status command quotes paths containing spaces");
        }
        check(File.ReadAllText(settings + ".before-aiannounce") == original, "Original agy settings are backed up");
        feed.Connect(@"C:\Program Files\AI Announce\AIAnnounce.exe");
        check(File.ReadAllText(settings + ".before-aiannounce") == original, "Repeated connection retains the original backup");
        DateTime now = DateTime.UtcNow;
        string payload = """{"email":"private@example.test","conversation_title":"private-title","access_token":"private-token","plan_tier":"Pro","quota":{"gemini-5h":{"remaining_fraction":0.65},"gemini-weekly":{"remaining_fraction":0.45},"non-gemini-weekly":{"remaining_fraction":0}}}""";
        feed.Capture(payload, now);
        var sample = feed.Read(now)!.Single();
        check(sample.Plan == "Pro" && sample.Live && Math.Abs(sample.Limits[0].Percent - 35) < .001 && Math.Abs(sample.Limits[1].Percent - 55) < .001,
            "Official status-line payload supplies Gemini session and weekly limits");
        string stored = File.ReadAllText(cache);
        check(!stored.Contains("private") && !stored.Contains("non-gemini"), "Feed persists only Gemini quotas without account, token or conversation data");
        feed.Capture("""{"quota":{},"agent_state":"initializing"}""", now.AddSeconds(1));
        check(File.ReadAllText(cache) == stored, "Initialization does not erase the last quota sample");
        check(feed.WaitingMessage().Contains("has not provided quota data"), "A running feed with no quotas is distinguished from an unstarted command");
        check(!feed.Read(now.AddMinutes(4))!.Single().Live, "An inactive feed is labeled cached");
        using var http = new HttpClient();
        var usage = new AgyUsage(http, () => throw new Exception("Credential access must not be needed"), dir, feed);
        check((await usage.ReadAsync(false)).Single().Limits[1].Percent == sample.Limits[1].Percent, "Normal local polling reads the feed without network or credentials");

        // Exercise the helper with a real stdin pipe, as agy invokes it.
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--feed-test");
        start.ArgumentList.Add(settings);
        start.ArgumentList.Add(cache);
        using var child = Process.Start(start)!;
        await child.StandardInput.WriteAsync(payload);
        child.StandardInput.Close();
        string display = await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(child.ExitCode == 0 && display.Length == 0 && feed.Read(DateTime.UtcNow)!.Single().Live,
            "Status-line helper consumes stdin quickly and leaves the terminal display unchanged");

        File.WriteAllText(settings, """{"statusLine":{"command":"existing-custom-command","enabled":true}}""");
        string custom = File.ReadAllText(settings);
        check(feed.Connect(@"C:\AIAnnounce.exe").Contains("left unchanged") && File.ReadAllText(settings) == custom,
            "An existing custom status-line command is never replaced");
        check(feed.Read(now) == null, "Disconnecting the feed stops it from supplying quota");

        // Uninstall restores the status line from before Connect and leaves later settings alone.
        File.WriteAllText(settings, original);
        File.Delete(settings + ".before-aiannounce");
        const string installed = @"C:\Users\someone\AppData\Local\Programs\AI Announce\AIAnnounce.exe";
        feed.Connect(installed);
        var changed = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        changed["model"] = "changed after connecting";
        File.WriteAllText(settings, changed.ToJsonString());
        check(!feed.Disconnect(@"C:\Other\AIAnnounce.exe") && feed.Connected, "Uninstalling another copy leaves the feed connected");
        check(feed.Disconnect(installed) && !feed.Connected, "Uninstall disconnects the feed it set up");
        using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
        {
            var line = doc.RootElement.GetProperty("statusLine");
            check(line.GetProperty("command").GetString() == "" && line.GetProperty("padding").GetInt32() == 2
                  && doc.RootElement.GetProperty("model").GetString() == "changed after connecting",
                "Uninstall restores the earlier status line and keeps newer agy settings");
        }
    }
}
