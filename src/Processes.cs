using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIAnnounce;

sealed record ProcEntry(int Pid, int ParentPid, string Exe);

sealed record ShellProc(int Pid, string Shell, string Command, DateTime Started);

static class Processes
{
    const int ProcessCommandLineInformation = 60;
    const int SystemProcessInformation = 5;
    const uint StatusInfoLengthMismatch = 0xC0000004;

    static readonly object SnapshotGate = new();
    static IntPtr snapshotBuffer;
    static int snapshotSize = 4 << 20;

    // One NtQuerySystemInformation call into a reused buffer. Toolhelp does the same query, then
    // marshals a 568-byte struct per process: ~30 ms with 900 processes against a few ms here.
    public static Dictionary<int, ProcEntry> Snapshot()
    {
        if (IntPtr.Size != 8) return ToolhelpSnapshot();  // field offsets below are x64
        lock (SnapshotGate)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (snapshotBuffer == IntPtr.Zero) snapshotBuffer = Marshal.AllocHGlobal(snapshotSize);
                int status = Native.NtQuerySystemInformation(SystemProcessInformation, snapshotBuffer, snapshotSize, out int needed);
                if (status == 0) return ParseProcessInformation(snapshotBuffer);
                if ((uint)status != StatusInfoLengthMismatch) break;
                Marshal.FreeHGlobal(snapshotBuffer);
                snapshotBuffer = IntPtr.Zero;
                snapshotSize = Math.Max(needed, snapshotSize) + (1 << 20);
            }
        }
        return ToolhelpSnapshot();
    }

    // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset @0, ImageName.Length @0x38,
    // ImageName.Buffer @0x40, UniqueProcessId @0x50, InheritedFromUniqueProcessId @0x58
    static Dictionary<int, ProcEntry> ParseProcessInformation(IntPtr buffer)
    {
        var map = new Dictionary<int, ProcEntry>(1024);
        IntPtr entry = buffer;
        while (true)
        {
            int next = Marshal.ReadInt32(entry, 0);
            int nameBytes = (ushort)Marshal.ReadInt16(entry, 0x38);
            IntPtr name = Marshal.ReadIntPtr(entry, 0x40);
            int pid = (int)Marshal.ReadIntPtr(entry, 0x50);
            int parent = (int)Marshal.ReadIntPtr(entry, 0x58);
            map[pid] = new ProcEntry(pid, parent, nameBytes > 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name, nameBytes / 2) : "");
            if (next == 0) break;
            entry += next;
        }
        return map;
    }

    static Dictionary<int, ProcEntry> ToolhelpSnapshot()
    {
        var map = new Dictionary<int, ProcEntry>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (!Native.Process32FirstW(snap, ref e)) return map;
            do map[(int)e.th32ProcessID] = new ProcEntry((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile);
            while (Native.Process32NextW(snap, ref e));
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return map;
    }

    // FILETIME of process creation, 0 if the process is gone or not accessible.
    public static long CreationTime(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return 0;
        try { return Native.GetProcessTimes(h, out long created, out _, out _, out _) ? created : 0; }
        finally { Native.CloseHandle(h); }
    }

    // The registry stores the creation FILETIME of each claude.exe, so a recycled PID never matches.
    public static bool IsAlive(int pid, long expectedStart)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!Native.GetExitCodeProcess(h, out uint code) || code != Native.STILL_ACTIVE) return false;
            return expectedStart == 0 ||
                   (Native.GetProcessTimes(h, out long created, out _, out _, out _) && created == expectedStart);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static string? CommandLine(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = 8192;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = Native.NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, size, out int needed);
                    if (status == 0)
                    {
                        // UNICODE_STRING { ushort Length; ushort MaximumLength; PWSTR Buffer } — Buffer is pointer-aligned
                        int length = (ushort)Marshal.ReadInt16(buffer);
                        IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                        return Marshal.PtrToStringUni(text, length / 2);
                    }
                    if ((uint)status != StatusInfoLengthMismatch || needed <= size) return null;
                    size = needed;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static string? ImagePath(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            uint size = (uint)name.Capacity;
            return QueryFullProcessImageNameW(h, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    // A TUI is a console program; a GUI build that shares the exe name (a desktop app) isn't. Reads the
    // Subsystem field of the PE optional header: 3 is console, 2 is windows. Unreadable counts as console.
    public static bool IsConsoleProgram(int pid)
    {
        if (ImagePath(pid) is not string path) return true;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[4096];
            int n = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (n < 0x40) return true;
            int pe = BitConverter.ToInt32(header, 0x3C);
            if (pe <= 0 || pe + 94 > n || BitConverter.ToUInt32(header, pe) != 0x00004550) return true;
            return BitConverter.ToUInt16(header, pe + 92) != 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    // Recognises the shells Claude Code's Bash and PowerShell tools spawn. MCP servers are also
    // children of claude.exe but never carry these markers.
    public static ShellProc? ToolShell(ProcEntry p, long created)
    {
        string exe = p.Exe.ToLowerInvariant();
        if (exe is not ("bash.exe" or "pwsh.exe" or "powershell.exe")) return null;
        string? cmdline = CommandLine(p.Pid);
        if (cmdline == null) return null;
        DateTime started = DateTime.FromFileTimeUtc(created);

        if (exe == "bash.exe")
        {
            if (cmdline.IndexOf("shell-snapshots", StringComparison.OrdinalIgnoreCase) < 0) return null;
            return new ShellProc(p.Pid, "bash", BashCommand(cmdline) ?? cmdline, started);
        }

        int enc = cmdline.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
        if (enc < 0 || cmdline.IndexOf("-NonInteractive", StringComparison.OrdinalIgnoreCase) < 0) return null;
        return new ShellProc(p.Pid, "pwsh", DecodeEncodedCommand(cmdline, enc + "-EncodedCommand".Length) ?? "", started);
    }

    // bash -c "source <snapshot> ... && eval '<command>' < /dev/null && pwd -P >| <cwd file>"
    // A ' inside the command is written '"'"' (and that arrives here as '\"'\"' after Windows
    // command-line escaping); '\'' handled too.
    static readonly string[] QuotedQuote = { "'\\\"'\\\"'", "'\"'\"'", "'\\''" };

    static string? BashCommand(string cmdline)
    {
        int start = cmdline.IndexOf("eval '", StringComparison.Ordinal);
        if (start < 0) return null;
        var sb = new StringBuilder();
        for (int i = start + 6; i < cmdline.Length;)
        {
            if (cmdline[i] != '\'')
            {
                sb.Append(cmdline[i++]);
                continue;
            }
            string? q = Array.Find(QuotedQuote, s => string.CompareOrdinal(cmdline, i, s, 0, s.Length) == 0);
            if (q == null) break;  // closing quote of the eval argument
            sb.Append('\'');
            i += q.Length;
        }
        return sb.Replace("\\\"", "\"").ToString().Trim();
    }

    static string? DecodeEncodedCommand(string cmdline, int from)
    {
        string rest = cmdline[from..].TrimStart();
        int end = rest.IndexOf(' ');
        string token = (end < 0 ? rest : rest[..end]).Trim('"');
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(token)).Trim(); }
        catch (FormatException) { return null; }
    }
}
