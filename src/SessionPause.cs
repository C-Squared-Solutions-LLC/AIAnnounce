using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIAnnounce;

// Cooperative pause, not process suspension: interrupt, ask the agent to wait, and keep that
// specific conversation stopped until Play. A process-wide freeze would miss Codex's shared server.
sealed class SessionPause
{
    internal const string PausePrompt = "AI Announce: Pause this session now. Stop active work and background agents. " +
        "Do not continue tasks or start more work until I explicitly ask you to resume. Acknowledge briefly, then wait.";
    internal const string ResumePrompt = "AI Announce: Resume the work that was paused. Continue the previous task from where you stopped, " +
        "including any agents you paused. Keep the existing permissions and ask me about any pending approval.";

    public sealed class Entry
    {
        public required SessionView Session { get; init; }
        public DateTime PausedAt { get; init; } = DateTime.UtcNow;
        public bool Notified { get; set; }
        public bool AgentsStopped { get; set; }
        public DateTime LastSent { get; set; }
        public string? Error { get; set; }
    }

    readonly List<Entry> entries = new();
    readonly Func<SessionView, SessionView?> current;
    readonly Func<SessionView, IEnumerable<ConsoleInput.Stroke>, Task<bool>> send;
    readonly Func<SessionView, bool> alive;
    readonly string path;
    readonly SemaphoreSlim gate = new(1, 1);
    public bool Changing { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<Entry> Entries => entries;
    public int Count => entries.Count;
    public event Action? Changed;

    public SessionPause(Func<SessionView, SessionView?> current,
        Func<SessionView, IEnumerable<ConsoleInput.Stroke>, Task<bool>> send, string path, Func<SessionView, bool>? alive = null)
    {
        this.current = current;
        this.send = send;
        this.alive = alive ?? (s => Processes.IsAlive(s.Pid, s.ProcStart));
        this.path = path;
        try
        {
            if (File.Exists(path)) entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = "Could not restore the paused session list."; }
    }

    public static bool Same(SessionView a, SessionView b) => a.Pid == b.Pid && a.ProcStart > 0 && a.ProcStart == b.ProcStart &&
        a.Source == b.Source && a.SessionId == b.SessionId;
    static bool Identified(SessionView s) => !string.IsNullOrEmpty(s.SessionId) &&
        s.SessionId != $"codex-{s.Pid}" && s.SessionId != $"agy-{s.Pid}";
    public Entry? Find(SessionView session) => entries.FirstOrDefault(e => Same(e.Session, session));
    public static bool CanPause(SessionView session) => session.ProcStart > 0 && Identified(session) &&
        (session.IsWorking || session.Items.Any(i => i.Kind == ItemKind.Agent));

    public async Task PauseAsync(IReadOnlyList<SessionView> sessions)
    {
        if (Changing) return;
        Changing = true;
        Error = null;
        Changed?.Invoke();
        await gate.WaitAsync();
        try
        {
            foreach (var target in sessions.Where(CanPause))
            {
                var live = current(target);
                if (live == null || !Same(target, live) || !CanPause(live) || Find(target) != null) continue;
                if (!await send(target, new[] { ConsoleInput.Escape }))
                { Error = $"Could not pause {target.Title}."; continue; }
                entries.Add(new Entry { Session = target, LastSent = DateTime.UtcNow });
                Save(); // preserve each success even if the next terminal is unreachable
            }
            Changed?.Invoke();
        }
        finally { Changing = false; gate.Release(); Changed?.Invoke(); }
    }

    // Called on every session update, including while the overlay is hidden. Only one action
    // runs at a time; Play cannot race an in-flight interrupt or pause instruction.
    public async Task MaintainAsync()
    {
        if (Changing || entries.Count == 0 || !await gate.WaitAsync(0)) return;
        bool changed = false;
        try
        {
            foreach (var entry in entries.ToList())
            {
                var live = current(entry.Session);
                if (!alive(entry.Session) || live != null && Identified(live) && !Same(entry.Session, live))
                { entries.Remove(entry); changed = true; continue; }
                if (live == null || !Identified(live)) continue; // a transient monitor failure must not lose the pause
                DateTime now = DateTime.UtcNow;
                if (now - entry.LastSent < TimeSpan.FromSeconds(entry.Notified ? 8 : 2)) continue;
                if (live.State == SessionState.Waiting)
                {
                    string message = "Waiting for your input; Play will not answer this prompt.";
                    changed |= entry.Error != message;
                    entry.Error = message;
                    continue;
                }
                if (!entry.AgentsStopped && live.Source == Cli.Claude && live.Items.Any(i => i.Kind == ItemKind.Agent))
                {
                    // Claude's confirmed stop-all-agents chord. Sending it with no live agents
                    // changes auto-reply settings, so recheck the current snapshot first.
                    bool ok = await send(entry.Session, new[] { ConsoleInput.Ctrl('x'), ConsoleInput.Ctrl('k'),
                        ConsoleInput.Pause(300), ConsoleInput.Ctrl('x'), ConsoleInput.Ctrl('k') });
                    entry.AgentsStopped = ok;
                    entry.Error = ok ? null : "Could not stop the background agents.";
                    entry.LastSent = now;
                    changed = true;
                    continue;
                }
                if (!entry.Notified && live.State is SessionState.Idle or SessionState.Asked)
                {
                    bool ok = await send(entry.Session, Prompt(entry.Session.Source, PausePrompt));
                    entry.Notified = ok;
                    entry.Error = ok ? null : "Could not send the pause instruction.";
                    entry.LastSent = now;
                    changed = true;
                }
                else if (live.IsWorking)
                {
                    bool ok = await send(entry.Session, new[] { ConsoleInput.Escape });
                    entry.Error = ok ? null : "Could not interrupt this session.";
                    entry.LastSent = now;
                    changed = true;
                }
            }
            if (changed) Save();
        }
        finally { gate.Release(); if (changed) Changed?.Invoke(); }
    }

    public async Task ResumeAsync()
    {
        if (Changing) return;
        Changing = true;
        Error = null;
        Changed?.Invoke();
        await gate.WaitAsync();
        try
        {
            foreach (var entry in entries.ToList())
            {
                var live = current(entry.Session);
                if (!alive(entry.Session) || live != null && Identified(live) && !Same(entry.Session, live))
                { entries.Remove(entry); Save(); continue; }
                if (live == null || !Identified(live)) { entry.Error = "Session is temporarily unavailable; click Play to retry."; continue; }
                if (live.State is not (SessionState.Idle or SessionState.Asked))
                {
                    entry.Error = live.State == SessionState.Waiting
                        ? "Answer the pending prompt in this terminal, then click Play again."
                        : "Still stopping; click Play again when this session is paused.";
                    continue;
                }
                if (await send(entry.Session, Prompt(entry.Session.Source, ResumePrompt)))
                { entries.Remove(entry); Save(); }
                else entry.Error = "Could not reach this terminal; click Play to retry.";
            }
            Save();
        }
        finally { Changing = false; gate.Release(); Changed?.Invoke(); }
    }

    internal static IEnumerable<ConsoleInput.Stroke> Prompt(Cli provider, string text)
    {
        // Close an idle terminal's model/help/usage panel before addressing its composer.
        yield return ConsoleInput.Escape;
        yield return ConsoleInput.Pause(150);
        // Claude restores the interrupted prompt to its composer; Ctrl+C clears it. Codex and
        // agy use the readline kill-to-start key. A beat before Enter prevents paste detection.
        yield return provider == Cli.Claude ? ConsoleInput.Ctrl('c') : ConsoleInput.Ctrl('u');
        yield return ConsoleInput.Pause(300);
        foreach (var stroke in ConsoleInput.Text(text)) yield return stroke;
        yield return ConsoleInput.Pause(400);
        yield return ConsoleInput.Enter;
    }

    void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "Paused in this app instance; could not save the list for an app restart."; }
    }
}
