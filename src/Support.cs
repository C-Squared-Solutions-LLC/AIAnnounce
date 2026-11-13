using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AIAnnounce;

static class AppVersion
{
    // Read the loaded assembly, so replacing the executable on disk cannot make
    // an older running overlay claim it has already updated.
    public static readonly string Current = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
}

static class UserPaths
{
    // Known-folder lookup can return an empty string in a restricted Windows token.
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } path
        ? path : Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
}

static class Json
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n)) return n;
        return 0;
    }

    // Claude writes some flags as strings ("run_in_background": "true")
    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    public static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static DateTime FromUnixMs(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.MinValue;

    public static DateTime Timestamp(JsonElement e) =>
        Str(e, "timestamp") is string s &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTime.UtcNow;

    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                string path = Path.Combine(Settings.Dir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

sealed class Settings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Collapsed { get; set; }
    public bool HideIdle { get; set; }
    public bool Hidden { get; set; }
    // a session needs you: toast with Windows' notification sound, plus the system Exclamation if Sound
    public bool Notifications { get; set; } = true;
    public bool Sound { get; set; }
    // a working session went idle: a silent toast, plus the finish chime if FinishedSound
    public bool FinishedNotify { get; set; } = true;
    public bool FinishedSound { get; set; }
    public bool TaskbarButton { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    // Off: no login tokens are read and no quota requests leave the machine.
    public bool PlanUsage { get; set; } = true;
    // "Claude|Fable wk" -> 90: pause that provider's sessions when the limit reaches 90%.
    public Dictionary<string, int> PauseAt { get; set; } = new();
    // limits that tripped and haven't dropped back below; true once you dismissed the red banner
    public Dictionary<string, bool> LimitTrips { get; set; } = new();

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIAnnounce");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("settings load: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("settings save: " + ex.Message);
        }
    }
}

static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AIAnnounce";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, false);
    }

    // Uninstall: drop the entry only when it starts this copy. Another build of the app may own it.
    public static void RemoveFor(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && string.Equals(value.Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write("autostart cleanup: " + ex.Message);
        }
    }
}
