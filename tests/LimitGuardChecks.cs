using AIAnnounce;

static class LimitGuardChecks
{
    public static void Run(Action<bool, string> check)
    {
        DateTime now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
        DateTime reset = now.AddDays(3);
        IReadOnlyList<UsageInfo> Usage(double week, double fable, Cli provider = Cli.Claude, DateTime? resetsAt = null) => new[]
        {
            new UsageInfo(new[]
            {
                new UsageLimit("Session", 0, null, "", false, "not reported"),
                new UsageLimit("Week", week, resetsAt ?? reset, ""),
                new UsageLimit("Fable wk", fable, resetsAt ?? reset, ""),
            }, "Max", now, true, provider),
        };

        var rules = new Dictionary<string, int> { ["Claude|Fable wk"] = 90 };
        var trips = new Dictionary<string, bool>();
        var guard = new LimitGuard(rules, trips);

        check(guard.Update(Usage(50, 89), now, out _).Count == 0 && guard.Active(Usage(50, 89)).Count == 0,
            "Below the setting nothing trips");
        var hit = guard.Update(Usage(50, 90), now, out bool changed);
        check(hit.Count == 1 && hit[0].Provider == Cli.Claude && hit[0].Label == "Fable wk" && hit[0].Threshold == 90 && changed,
            "Reaching the setting trips once, for that provider and limit");
        check(guard.Active(Usage(50, 90)).Count == 1, "A tripped limit turns the overlay red");
        check(guard.Update(Usage(50, 95), now, out changed).Count == 0 && !changed,
            "Still over the setting doesn't pause again, so Play on a full account sticks");

        var reopened = new LimitGuard(rules, trips);
        check(reopened.Update(Usage(50, 95), now, out _).Count == 0, "A restarted overlay remembers the trip and doesn't pause again");

        check(guard.Dismiss() && guard.Active(Usage(50, 95)).Count == 0, "Dismissing clears the red state");
        check(guard.Update(Usage(50, 12), now, out changed).Count == 0 && changed && trips.Count == 0,
            "A new account below the setting clears the trip");
        check(guard.Update(Usage(50, 91), now, out _).Count == 1, "Crossing again after that pauses again");

        trips.Clear();
        check(guard.Update(new[] { UsageInfo.Unavailable(Cli.Claude, "offline") }, now, out changed).Count == 0 && !changed,
            "Unreported usage neither trips nor clears");
        check(guard.Update(Usage(50, 99, resetsAt: now.AddMinutes(-5)), now, out _).Count == 0,
            "A limit whose reset already passed is stale and doesn't trip");
        check(guard.Update(Usage(99, 99, Cli.Codex), now, out _).Count == 0,
            "A rule for Claude ignores the same label on Codex");

        guard.Update(Usage(50, 95), now, out _);
        rules.Remove("Claude|Fable wk");
        guard.Update(Usage(50, 95), now, out changed);
        check(changed && trips.Count == 0 && guard.Active(Usage(50, 95)).Count == 0, "Turning a rule off drops its red state");
    }
}
