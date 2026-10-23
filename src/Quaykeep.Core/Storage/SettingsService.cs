using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Storage;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new(VaultService.Json) { WriteIndented = true };

    public const int CurrentVersion = 1;

    public AppSettings Settings { get; private set; } = new() { SettingsVersion = CurrentVersion };
    public bool IsFirstRun { get; private set; }

    public void Load()
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            IsFirstRun = true;
            Settings = new AppSettings { SettingsVersion = CurrentVersion };
            return;
        }
        try
        {
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options)
                       ?? new AppSettings();
        }
        catch (JsonException)
        {
            Settings = new AppSettings { SettingsVersion = CurrentVersion };
        }
        Migrate(Settings);
    }

    /// <summary>Upgrades settings written by older versions (in place).</summary>
    public static bool Migrate(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentVersion) return false;
        if (s.SettingsVersion < 1 && s.MonitorIntervalMinutes == 10) s.MonitorIntervalMinutes = 5; // old default
        s.SettingsVersion = CurrentVersion;
        return true;
    }

    public void Save() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Settings, Options));
}

/// <summary>
/// "Start with the session"; started that way the app stays in the tray (--tray). No admin rights needed anywhere:
/// Windows: the per-user Run key. macOS: a LaunchAgent in ~/Library/LaunchAgents. Linux: an XDG autostart entry.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Quaykeep";
    /// <summary>The entry of SSH Manager, the name before Quaykeep: it starts an exe that is gone after the update.</summary>
    private const string LegacyValueName = "SshManager";
    private const string MacLabel = "app.quaykeep";

    public static string Command => $"\"{AppPaths.Launcher}\" --tray";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MacPlist => Path.Combine(Home, "Library", "LaunchAgents", MacLabel + ".plist");
    private static string LinuxEntry => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".config"),
        "autostart", "quaykeep.desktop");

    public static bool IsEnabled() =>
        OperatingSystem.IsWindows() ? IsEnabledWindows() : File.Exists(OperatingSystem.IsMacOS() ? MacPlist : LinuxEntry);

    /// <summary>Creates, refreshes (if the program moved) or removes the entry; on Windows drops the one from before the rename.</summary>
    public static void Apply(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(enabled);
            return;
        }
        var path = OperatingSystem.IsMacOS() ? MacPlist : LinuxEntry;
        if (!enabled)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var text = OperatingSystem.IsMacOS() ? MacAgent(AppPaths.Launcher) : DesktopEntry(AppPaths.Launcher);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(LegacyValueName) != null) key.DeleteValue(LegacyValueName);
        if (enabled)
        {
            if (key.GetValue(ValueName) as string != Command)
                key.SetValue(ValueName, Command);
        }
        else if (key.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName);
        }
    }

    private static string MacAgent(string exe) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{MacLabel}</string>
          <key>ProgramArguments</key>
          <array><string>{SecurityElement.Escape(exe)}</string><string>--tray</string></array>
          <key>RunAtLoad</key><true/>
          <key>ProcessType</key><string>Interactive</string>
        </dict>
        </plist>
        """;

    // Exec quoting per the Desktop Entry spec: inside double quotes, ", `, $ and \ are escaped with a backslash.
    private static string DesktopEntry(string exe) => $"""
        [Desktop Entry]
        Type=Application
        Name=Quaykeep
        Comment=Quaykeep in the tray
        Exec="{exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$")}" --tray
        Terminal=false
        X-GNOME-Autostart-enabled=true
        """;
}
