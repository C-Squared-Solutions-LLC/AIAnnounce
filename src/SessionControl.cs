using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIAnnounce;

// Acts on sessions the way you would at their keyboard: Esc stops the running turn, ctrl+x ctrl+k
// (pressed twice, it asks for confirmation) stops background agents, /exit quits, and
// `claude --resume <id>` typed into the shell brings the conversation back on the new build.
//
// Things learned against a live 2.1.316 session: ctrl+l is wired to clear-screen, not clear-input
// (ctrl+c clears the box); ctrl+d didn't exit at all; and the kill-agents chord with no agents
// running "disarms artifact auto-replies", a notice that lands as a message and starts a new turn.
static class SessionControl
{
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "pwsh.exe", "powershell.exe", "cmd.exe", "bash.exe" };

    // flags whose next argument is their value; everything else is treated as a switch
    static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--model", "--fallback-model", "--permission-mode", "--add-dir", "--settings", "--setting-sources",
        "--mcp-config", "--agent", "--agents", "--append-system-prompt", "--system-prompt", "--plugin-dir",
        "--allowedTools", "--allowed-tools", "--disallowedTools", "--disallowed-tools", "--effort", "--max-turns",
    };

    public static bool IsShell(string? exe) => exe != null && Shells.Contains(exe);

    // Emergency stop. Esc goes out to every session first so they all halt together. Sessions with
    // background agents then get the kill-agents chord, once a lone Esc has registered (sent right
    // after it, Ctrl+X would parse as Alt+Ctrl+X). Stopped agents report back with notifications
    // that can wake the model, so for a few seconds any stopped session that goes busy again gets
    // one more Esc.
    public static Task Stop(IReadOnlyList<SessionView> sessions, Func<int, string?> statusOf) => Task.Run(() =>
    {
        foreach (var s in sessions)
            Report(s, "stop: esc", ConsoleInput.Send(s.Pid, new[] { ConsoleInput.Escape }));

        // ctrl+x ctrl+k is Claude's; Codex and agy stop on Esc alone
        var withAgents = sessions.Where(s => s.Source == Cli.Claude && s.Items.Any(i => i.Kind == ItemKind.Agent)).ToList();
        if (withAgents.Count > 0)
        {
            Thread.Sleep(450);
            var killAgents = new[]
            {
                ConsoleInput.Ctrl('x'), ConsoleInput.Ctrl('k'), ConsoleInput.Pause(300), ConsoleInput.Ctrl('x'), ConsoleInput.Ctrl('k'),
            };
            foreach (var s in withAgents)
                Report(s, "stop: kill agents", ConsoleInput.Send(s.Pid, killAgents));
        }

        var retries = sessions.ToDictionary(s => s.Pid, _ => 2);
        var until = DateTime.UtcNow.AddSeconds(10);
        Thread.Sleep(1500);  // let the registry catch up with the first Esc
        while (DateTime.UtcNow < until && retries.Values.Any(n => n > 0))
        {
            foreach (var s in sessions)
            {
                if (retries[s.Pid] == 0 || statusOf(s.Pid) != "busy") continue;
                retries[s.Pid]--;
                Report(s, "stop: busy again, esc", ConsoleInput.Send(s.Pid, new[] { ConsoleInput.Escape }));
            }
            Thread.Sleep(700);
        }
    });

    // Exit an idle session and resume it in the same shell, picking up whatever build is installed.
    public static Task<bool> Restart(SessionView s, Func<int, string?> statusOf) => Task.Run(() =>
    {
        string? command = ResumeCommand(s);
        if (command == null || s.ShellPid == 0)
        {
            Log.Write($"restart {s.Pid}: no shell to resume in");
            return false;
        }
        // the click may be a few seconds old; never type into a session that started working since
        if (statusOf(s.Pid) != "idle")
        {
            Log.Write($"restart {s.Pid}: no longer idle, skipped");
            return false;
        }

        // ctrl+c empties a half-typed draft (on an empty box it only shows a hint), then /exit;
        // the pause lets the command menu settle so Enter runs /exit itself
        var exit = new[] { ConsoleInput.Ctrl('c'), ConsoleInput.Pause(300) }
            .Concat(ConsoleInput.Text("/exit"))
            .Append(ConsoleInput.Pause(500))
            .Append(ConsoleInput.Enter);
        if (!ConsoleInput.Send(s.Pid, exit))
        {
            Log.Write($"restart {s.Pid}: couldn't reach its console");
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Processes.IsAlive(s.Pid, s.ProcStart) && DateTime.UtcNow < deadline) Thread.Sleep(200);
        if (Processes.IsAlive(s.Pid, s.ProcStart))
        {
            Log.Write($"restart {s.Pid}: claude didn't exit within 20 s, left alone");
            return false;
        }

        Thread.Sleep(500);  // let the shell get its prompt back
        // keys arriving in one burst read as a paste; a beat before Enter keeps it a submit
        bool typed = ConsoleInput.Send(s.ShellPid, ConsoleInput.Text(command).Append(ConsoleInput.Pause(300)).Append(ConsoleInput.Enter));
        Log.Write($"restart {s.Pid}: {(typed ? "resumed with" : "FAILED to type")} {command}");
        return typed;
    });

    public static string? ResumeCommand(SessionView s)
    {
        if (s.LaunchCommand == null || !IsShell(s.ShellExe)) return null;
        var argv = SplitCommandLine(s.LaunchCommand);
        if (argv.Count == 0) return null;

        var args = new List<string>();
        for (int i = 1; i < argv.Count; i++)
        {
            string a = argv[i];
            if (a is "-p" or "--print") return null;  // headless run, nothing to resume into
            if (a is "--resume" or "-r" or "--session-id")
            {
                if (i + 1 < argv.Count && !argv[i + 1].StartsWith('-')) i++;
                continue;
            }
            if (a is "--continue" or "-c" or "--fork-session" || a.StartsWith("--resume=") || a.StartsWith("--session-id=")) continue;
            if (!a.StartsWith('-')) continue;  // a positional prompt would be sent all over again
            args.Add(a);
            if (ValueFlags.Contains(a) && i + 1 < argv.Count) args.Add(argv[++i]);
        }
        args.Add("--resume");
        args.Add(s.SessionId);

        string exe = argv[0];
        return s.ShellExe!.ToLowerInvariant() switch
        {
            "pwsh.exe" or "powershell.exe" => "& " + string.Join(" ", args.Prepend(exe).Select(PowerShellQuote)),
            "cmd.exe" => string.Join(" ", args.Prepend(exe).Select(CmdQuote)),
            _ => string.Join(" ", args.Prepend(MsysPath(exe)).Select(ShQuote)),
        };
    }

    // Newest Claude Code build on disk: the native installer keeps one file per version.
    public static Version? LatestInstalled(string claudeDir)
    {
        Version? best = null;
        string versions = Path.Combine(UserPaths.Home, ".local", "share", "claude", "versions");
        try
        {
            if (Directory.Exists(versions))
                foreach (string entry in Directory.EnumerateFileSystemEntries(versions))
                    if (Version.TryParse(Path.GetFileName(entry), out var v) && (best == null || v > best)) best = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            string record = Path.Combine(claudeDir, ".last-update-result.json");
            if (File.Exists(record))
            {
                using var doc = JsonDocument.Parse(Json.ReadShared(record));
                if (Json.Str(doc.RootElement, "outcome") == "success" &&
                    Version.TryParse(Json.Str(doc.RootElement, "version_to"), out var v) && (best == null || v > best))
                    best = v;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return best;
    }

    static void Report(SessionView s, string what, bool ok) =>
        Log.Write($"{what} -> {s.Title} (pid {s.Pid}): {(ok ? "sent" : "FAILED")}");

    static string PowerShellQuote(string a) =>
        Regex.IsMatch(a, @"^[A-Za-z0-9_\-.:=/]+$") ? a : "'" + a.Replace("'", "''") + "'";

    static string CmdQuote(string a) => a.IndexOfAny(new[] { ' ', '&', '|', '<', '>', '^', '(', ')' }) >= 0 ? "\"" + a + "\"" : a;

    static string ShQuote(string a) =>
        Regex.IsMatch(a, @"^[A-Za-z0-9_\-.:=/]+$") ? a : "'" + a.Replace("'", "'\\''") + "'";

    static string MsysPath(string path) =>
        path.Length > 2 && path[1] == ':' ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') : path.Replace('\\', '/');

    public static List<string> SplitCommandLine(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return new List<string>();
        try
        {
            var list = new List<string>(count);
            for (int i = 0; i < count; i++)
                list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "");
            return list;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
