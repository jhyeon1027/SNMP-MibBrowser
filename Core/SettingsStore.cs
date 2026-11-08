using System.Text.Json;

namespace SnmpMibBrowser;

/// <summary>
/// settings.json shared by both apps (same format, so the file can be copied between Windows and Linux).
/// Windows: %LOCALAPPDATA%\SNMP-MibBrowser. Linux: $XDG_CONFIG_HOME/snmp-mibbrowser (default ~/.config/snmp-mibbrowser).
/// </summary>
public sealed class SettingsStore
{
    public static string DefaultDirectory { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SNMP-MibBrowser")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "snmp-mibbrowser");

    public string FilePath { get; }
    public string Directory => Path.GetDirectoryName(FilePath)!;
    public string ErrorLogPath => Path.Combine(Directory, "error.log");

    public SettingsStore(string? path = null) => FilePath = path ?? Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Reads the settings; warning is set when the file existed but could not be read (it is kept as settings.json.bak).</summary>
    public UserSettings Load(out string? warning)
    {
        warning = null;
        var settings = new UserSettings();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) is { } s) settings = s;
        }
        catch (Exception ex)
        {
            // Keep the unreadable file instead of overwriting it with defaults on the next save.
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
            warning = ex.Message;
        }
        if (settings.Profiles.Count == 0)
        {
            settings.Profiles.Add(new SnmpProfile { Name = "Default" });
            settings.SelectedProfile = "Default";
        }
        return settings;
    }

    /// <summary>Writes via a temporary file so a crash during the write cannot leave a truncated settings.json.
    /// On Linux the file is readable by the owner only (it holds communities and v3 keys).</summary>
    public bool Save(UserSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, FilePath, true);
            return true;
        }
        catch { return false; }
    }

    public void WriteErrorLog(string source, Exception? ex, Version? version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} error (v{version}){Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
