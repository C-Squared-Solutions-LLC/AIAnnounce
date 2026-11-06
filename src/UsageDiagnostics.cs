using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace AIAnnounce;

// Only formatted quota results are included: never credentials, account IDs or HTTP bodies.
static class UsageDiagnostics
{
    public static string Format(IReadOnlyList<UsageInfo> usage)
    {
        var text = new StringBuilder($"AI Announce {AppVersion.Current} - usage details{Environment.NewLine}");
        if (usage.Count == 0) text.AppendLine("Checking provider usage...");
        foreach (var info in usage)
        {
            text.AppendLine();
            string status = info.FetchedAt == DateTime.MinValue ? "unavailable"
                : $"{(info.Live ? "live" : "cached")} as of {info.FetchedAt.ToLocalTime():g}";
            text.AppendLine($"{info.DisplayName}: {status}" + (info.Plan == null ? "" : $", plan {info.Plan}"));
            foreach (var limit in info.Limits)
                text.AppendLine(limit.Available
                    ? $"  {limit.Label}: {limit.Percent:0.#}% used" + (limit.ResetsAt is DateTime reset ? $", resets {reset.ToLocalTime():g}" : "")
                    : $"  {limit.Label}: unavailable" + (limit.Note != null && limit.Note != info.Message ? $" ({limit.Note})" : ""));
            if (info.Message != null) text.AppendLine("  " + info.Message);
        }
        return text.ToString();
    }

    public static void Save(IReadOnlyList<UsageInfo> usage, bool command = false)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(Path.Combine(Settings.Dir, command ? "usage-command.txt" : "usage-status.txt"), Format(usage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool WriteConsole(string text)
    {
        if (!ConsoleOut.Use()) return false;
        Console.Out.Write(text);
        return true;
    }
}

// A WinExe started from a shell has no stdout unless it attaches to that shell's console, and .NET 8
// throws if you set Console.OutputEncoding without one (the self-contained build runs on 8). Keep a
// redirected pipe as it is, attach otherwise, never allocate a console, and write UTF-8 either way
// without touching the console's code page.
static class ConsoleOut
{
    const int StdOutput = -11;
    const uint FileTypeUnknown = 0, AttachParentProcess = uint.MaxValue;

    public static bool Use()
    {
        if (GetFileType(GetStdHandle(StdOutput)) == FileTypeUnknown)
        {
            if (!Native.AttachConsole(AttachParentProcess)) return false;
            Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
            if (GetFileType(GetStdHandle(StdOutput)) == FileTypeUnknown) return false;
        }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        return true;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr handle);
}

sealed class UsageDetailsForm : Form
{
    readonly UsageMonitor monitor;
    readonly TextBox report;
    readonly Button refresh;

    public UsageDetailsForm(UsageMonitor monitor)
    {
        this.monitor = monitor;
        Text = "AI Announce - Usage details";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        MinimizeBox = false;
        ClientSize = new Size(700, 440);
        MinimumSize = new Size(480, 300);
        report = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10), Text = UsageDiagnostics.Format(monitor.Last),
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6),
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var copy = new Button { Text = "Copy details", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(report.Text); copy.Text = "Copied"; }
            catch (ExternalException) { copy.Text = "Try copying again"; }
        };
        refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += async (_, _) => await RefreshUsage();
        var connect = new Button { Text = "Connect Gemini usage", AutoSize = true };
        connect.Click += (_, _) =>
        {
            try { report.Text = new AgyUsageFeed().Connect(Environment.ProcessPath!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { report.Text = "Could not update agy's status-line settings. Check that settings.json is writable and contains valid JSON."; }
        };
        buttons.Controls.AddRange(new Control[] { close, copy, refresh, connect });
        Controls.Add(report);
        Controls.Add(buttons);
        CancelButton = close;
        Shown += async (_, _) => { if (monitor.Last.Count == 0) await RefreshUsage(); };
    }

    async System.Threading.Tasks.Task RefreshUsage()
    {
        refresh.Enabled = false;
        refresh.Text = "Checking...";
        try
        {
            var usage = await monitor.RefreshAsync();
            if (IsDisposed) return;
            report.Text = UsageDiagnostics.Format(usage);
            UsageDiagnostics.Save(usage);
        }
        finally
        {
            if (!IsDisposed) { refresh.Enabled = true; refresh.Text = "Refresh"; }
        }
    }
}
