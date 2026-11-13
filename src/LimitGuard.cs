using System;
using System.Collections.Generic;
using System.Linq;

namespace AIAnnounce;

// "Pause everything when Claude's Fable weekly limit reaches 90%." Rules are keyed by provider
// and limit label ("Claude|Fable wk" -> 90). A rule trips once when the limit reaches its
// threshold and stays tripped until that limit is seen below it again: the window reset, or
// you switched to an account with room left. So pressing Play on a still-full account doesn't
// get re-paused on the next refresh, and an overlay restart doesn't pause everything again.
sealed class LimitGuard
{
    public sealed record Trip(string Key, Cli Provider, string ProviderName, string Label, double Percent, int Threshold);

    public static string KeyFor(Cli provider, string label) => $"{provider}|{label}";

    public static readonly int[] Choices = { 50, 70, 80, 85, 90, 95, 99, 100 };

    // trips: key -> dismissed. Persisted in settings with the rules.
    readonly Dictionary<string, int> rules;
    readonly Dictionary<string, bool> trips;

    public LimitGuard(Dictionary<string, int> rules, Dictionary<string, bool> trips)
    {
        this.rules = rules;
        this.trips = trips;
    }

    // Limits that just reached their threshold. Also forgets trips whose limit has dropped back
    // below. A limit that isn't reported right now leaves its trip alone either way.
    // A limit whose reset time has passed is old news and counts as unreported.
    public List<Trip> Update(IReadOnlyList<UsageInfo> usage, DateTime now, out bool changed)
    {
        changed = false;
        var tripped = new List<Trip>();
        foreach (var info in usage)
        foreach (var limit in info.Limits.Where(l => l.Available && !(l.ResetsAt <= now)))
        {
            string key = KeyFor(info.Provider, limit.Label);
            if (!rules.TryGetValue(key, out int threshold) || threshold <= 0) continue;
            bool over = limit.Percent >= threshold;
            if (over && !trips.ContainsKey(key))
            {
                trips[key] = false;
                changed = true;
                tripped.Add(new Trip(key, info.Provider, info.ProviderName, limit.Label, limit.Percent, threshold));
            }
            else if (!over && trips.Remove(key)) changed = true;
        }
        // a rule you switched off takes its trip with it
        foreach (string key in trips.Keys.Where(k => !rules.ContainsKey(k)).ToList()) { trips.Remove(key); changed = true; }
        return tripped;
    }

    // Tripped limits that haven't been dismissed: these turn the overlay red.
    public List<Trip> Active(IReadOnlyList<UsageInfo> usage)
    {
        var active = new List<Trip>();
        foreach (var (key, dismissed) in trips)
        {
            if (dismissed || !rules.TryGetValue(key, out int threshold)) continue;
            var (info, limit) = Find(usage, key);
            string[] parts = key.Split('|', 2);
            Cli provider = info?.Provider ?? (Enum.TryParse(parts[0], out Cli p) ? p : Cli.Claude);
            string name = info?.ProviderName ?? provider switch { Cli.Codex => "Codex", Cli.Agy => "Gemini", _ => "Claude" };
            active.Add(new Trip(key, provider, name, parts.Length > 1 ? parts[1] : key, limit?.Percent ?? threshold, threshold));
        }
        return active;
    }

    public bool Dismiss()
    {
        bool any = false;
        foreach (string key in trips.Keys.Where(k => !trips[k]).ToList()) { trips[key] = true; any = true; }
        return any;
    }

    public static int? Threshold(Dictionary<string, int> rules, Cli provider, string label) =>
        rules.TryGetValue(KeyFor(provider, label), out int t) && t > 0 ? t : null;

    static (UsageInfo?, UsageLimit?) Find(IReadOnlyList<UsageInfo> usage, string key)
    {
        foreach (var info in usage)
        foreach (var limit in info.Limits)
            if (KeyFor(info.Provider, limit.Label) == key) return (info, limit.Available ? limit : null);
        return (null, null);
    }
}
