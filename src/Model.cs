using System;
using System.Collections.Generic;

namespace AIAnnounce;

enum SessionState { Busy, Idle, Waiting, Asked }

enum Cli { Claude, Codex, Agy }

enum ItemKind { Agent, Shell, BackgroundShell }

sealed record GpuSample(string Name, ulong Used, ulong Total, uint Utilization);

sealed class ItemView
{
    public required ItemKind Kind { get; init; }
    public required string Label { get; init; }
    public string? Detail { get; init; }     // agent type, or the shell exe
    public string? Activity { get; init; }   // an agent's latest tool call
    public string? Command { get; init; }    // full command line for the tooltip
    public DateTime Started { get; init; }
}

sealed class SessionView
{
    public required string SessionId { get; init; }
    public required int Pid { get; init; }
    public Cli Source { get; init; } = Cli.Claude;
    public required string Title { get; init; }
    public IReadOnlyList<string> TabTitles { get; init; } = Array.Empty<string>();  // what its terminal tab may be called
    public required string Project { get; init; }
    public required string Cwd { get; init; }
    public SessionState State { get; init; }
    public string StatusText { get; init; } = "";
    public DateTime Since { get; init; }
    public string? Attention { get; init; }
    public string? AttentionKind { get; init; }  // permission | question | plan | input | asked | dialog | other
    public string? Activity { get; init; }
    public DateTime ActivitySince { get; init; }
    public List<ItemView> Items { get; init; } = new();
    public string? Entrypoint { get; init; }
    public string? Version { get; init; }
    public string? TranscriptPath { get; init; }
    public long ProcStart { get; init; }
    public string? LaunchCommand { get; init; }   // claude.exe's own command line
    public int ShellPid { get; init; }            // the shell it was started from, 0 if unknown
    public string? ShellExe { get; init; }
    public string? LatestVersion { get; init; }   // newest build installed, when newer than Version

    public bool NeedsUser => State is SessionState.Waiting or SessionState.Asked;
    public bool NeedsRestart => LatestVersion != null;
    public bool IsWorking => State == SessionState.Busy;
    // only from a shell prompt we can type the resume command into, and never mid-turn
    public bool CanRestart => Source == Cli.Claude && State == SessionState.Idle && ShellPid != 0 && SessionControl.IsShell(ShellExe);
    public string Product => Source switch { Cli.Codex => "Codex", Cli.Agy => "Gemini (agy)", _ => "Claude Code" };

    // One key per "needs you" episode; statusUpdatedAt changes every time the status flips.
    public string AttentionKey => $"{SessionId}|{Since.Ticks}|{State}";
}

sealed class Snapshot
{
    public static readonly Snapshot Empty = new(Array.Empty<SessionView>(), Array.Empty<GpuSample>(), DateTime.UtcNow);

    public Snapshot(IReadOnlyList<SessionView> sessions, IReadOnlyList<GpuSample> gpus, DateTime at)
    {
        Sessions = sessions;
        Gpus = gpus;
        At = at;
    }

    public IReadOnlyList<SessionView> Sessions { get; }
    public IReadOnlyList<GpuSample> Gpus { get; }
    public DateTime At { get; }
}
