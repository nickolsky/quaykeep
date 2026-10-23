using System.Security.Cryptography;
using System.Text;

namespace Quaykeep.Core;

/// <summary>
/// Locates the data folder, the program files and the per-user pipes.
/// The data folder is, in this order:
/// <list type="number">
/// <item>QUAYKEEP_DATA;</item>
/// <item>"data" next to Quaykeep.sln above the exe: a dev build under src\...\bin and a copy published into Quaykeep\app share it;</item>
/// <item>the per-user folder (Windows %LOCALAPPDATA%\Quaykeep\data, Linux ~/.local/share/Quaykeep/data, macOS
/// ~/Library/Application Support/Quaykeep/data) when the program was installed: an installer copy is replaced on every
/// update, an AppImage is a read-only mount and a macOS app bundle is no place for data;</item>
/// <item>otherwise "data" next to the program (an unpacked zip: portable).</item>
/// </list>
/// A side-by-side test build (instance.txt next to the exe, e.g. "dev") gets its own data folder (data-dev),
/// pipes and single-instance lock, so it runs next to the main copy without touching it.
/// </summary>
public static class AppPaths
{
    public const string SolutionMarker = "Quaykeep.sln";
    /// <summary>The solution file before the rename to Quaykeep: a checkout of that age keeps finding its data folder.</summary>
    public const string LegacySolutionMarker = "SSHManagement.sln";
    public const string InstanceFile = "instance.txt";
    /// <summary>Velopack package id and install folder (%LOCALAPPDATA%\QuaykeepApp on Windows). Not "Quaykeep": that folder holds the data, and uninstall deletes the install folder.</summary>
    public const string PackId = "QuaykeepApp";

    /// <summary>Name of a side-by-side test instance, null for the main one.</summary>
    public static string? Instance { get; } = ResolveInstance();
    public static bool IsSideBySide => Instance != null;
    /// <summary>"" for the main instance, "-dev" for a test one: appended to pipe and lock names.</summary>
    public static string InstanceSuffix => Instance == null ? "" : "-" + Instance;
    /// <summary>"Quaykeep" or "Quaykeep [dev]".</summary>
    public static string ProductTitle => Instance == null ? "Quaykeep" : $"Quaykeep [{Instance}]";

    /// <summary>The program runs from an installer's copy (Setup, .pkg, AppImage) rather than from an unpacked folder.</summary>
    public static bool IsInstalled { get; } = ResolveInstalled();

    public static string DataDir { get; } = ResolveDataDir();
    public static string VaultFile => Path.Combine(DataDir, "vault.dat");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    public static string PublicKeyDir => Path.Combine(DataDir, "pub");
    /// <summary>The built-in terminal's and editor's browser profile: next to the data, never inside it (backups zip the data folder).</summary>
    public static string WebViewDir => Path.Combine(Path.GetDirectoryName(DataDir)!, "webview" + InstanceSuffix);

    public static string ExeDir => AppContext.BaseDirectory;
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
    public static string MainExe => Path.Combine(ExeDir, Exe("Quaykeep"));
    private static string HelperFile => Path.Combine(ExeDir, Exe("qk"));

    /// <summary>
    /// The path that starts Quaykeep and stays valid: inside an AppImage the process runs from a temporary mount, so the
    /// AppImage file itself (APPIMAGE). Used for autostart, for MCP client configs and by qk to start the app.
    /// </summary>
    public static string Launcher => AppImage ?? MainExe;

    private static string? AppImage => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } p ? p : null;

    /// <summary>
    /// The qk helper as a command: qk(.exe) next to the program, or "&lt;AppImage&gt; qk" (the main program runs the helper
    /// when its first argument is "qk"; the helper's own file inside the mount changes on every start).
    /// </summary>
    public static (string File, string[] Args) Helper => AppImage is { } image ? (image, ["qk"]) : (HelperFile, []);

    /// <summary>The helper as a single program path: SSH_ASKPASS takes no arguments (the AppImage runs the helper by the askpass variable).</summary>
    public static string HelperExe => Helper.File;

    /// <summary>The helper command line as a user would type it.</summary>
    public static string HelperCommand => string.Join(' ', new[] { Quote(Helper.File) }.Concat(Helper.Args));

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// Windows: the standard OpenSSH agent pipe; a test instance never takes it. Linux / macOS: a socket for SSH_AUTH_SOCK
    /// ($XDG_RUNTIME_DIR/quaykeep-agent.sock, or ~/.quaykeep-agent.sock).
    /// </summary>
    public static string AgentPipeDefault => !OperatingSystem.IsWindows() ? AgentSocket : Instance == null ? "openssh-ssh-agent" : AgentPipeFallback;
    /// <summary>
    /// The agent's own pipe. SSH Manager (the name before Quaykeep) told users to point SSH_AUTH_SOCK at
    /// "sshmanager-agent-&lt;user&gt;": while it still does, that name is kept so their ssh keeps working.
    /// </summary>
    public static string AgentPipeFallback => !OperatingSystem.IsWindows() ? AgentSocket
        : LegacyAgentPipeInUse ? LegacyAgentPipe : "quaykeep-agent-" + SafeUser + InstanceSuffix;

    private static string AgentSocket =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && Directory.Exists(run)
            ? Path.Combine(run, $"quaykeep-agent{InstanceSuffix}.sock")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".quaykeep-agent{InstanceSuffix}.sock");

    /// <summary>
    /// The control pipe of qk. Linux / macOS map a pipe to a socket in the temp folder, and the whole path must fit in
    /// 104 bytes (macOS's temp folder alone takes ~50), so there it is a short hash of the user and the instance.
    /// </summary>
    public static string ControlPipe => OperatingSystem.IsWindows()
        ? "quaykeep-ctl-" + SafeUser + InstanceSuffix
        : "qk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + InstanceSuffix)))[..12].ToLowerInvariant();
    // Kept from SSH Manager on purpose: an old copy and a new one must never run at once (same data folder and agent pipe).
    public static string SingleInstanceLock => @"Local\SshManager-" + Environment.UserName + InstanceSuffix;

    private static string LegacyAgentPipe => "sshmanager-agent-" + SafeUser + InstanceSuffix;

    private static bool LegacyAgentPipeInUse =>
        OperatingSystem.IsWindows() &&
        (Environment.GetEnvironmentVariable("SSH_AUTH_SOCK", EnvironmentVariableTarget.User) ?? "")
        .EndsWith(LegacyAgentPipe, StringComparison.OrdinalIgnoreCase);

    /// <summary>The environment variable, or the one SSH Manager used before the rename.</summary>
    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable("QUAYKEEP_" + name) is { Length: > 0 } v ? v : Environment.GetEnvironmentVariable("SSHMANAGER_" + name);

    internal static string SafeUser =>
        new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).ToLowerInvariant();

    private static string? ResolveInstance()
    {
        var name = Env("INSTANCE");
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, InstanceFile);
                if (File.Exists(file)) name = File.ReadAllText(file);
            }
            catch (IOException)
            {
            }
        }
        name = name?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(name) && name.Length <= 16 && name.All(c => char.IsAsciiLetterOrDigit(c)) ? name : null;
    }

    /// <summary>
    /// Windows: Velopack's layout %LOCALAPPDATA%\QuaykeepApp\current (its portable zip has the same layout anywhere else:
    /// that one keeps its data next to it). Linux: an AppImage. macOS: an app bundle.
    /// </summary>
    private static bool ResolveInstalled()
    {
        if (AppImage != null) return true;
        var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (OperatingSystem.IsMacOS()) return exeDir.EndsWith(".app/Contents/MacOS", StringComparison.Ordinal);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PackId);
        return string.Equals(Path.GetDirectoryName(exeDir), root, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(exeDir).Equals("current", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDataDir()
    {
        var env = Env("DATA");
        if (!string.IsNullOrWhiteSpace(env))
            return Ensure(Path.GetFullPath(env));

        var folder = "data" + InstanceSuffix;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionMarker)) || File.Exists(Path.Combine(dir.FullName, LegacySolutionMarker)))
                return Ensure(Path.Combine(dir.FullName, folder));
        }
        if (IsInstalled)
            return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "Quaykeep", folder));
        // Velopack's portable zip (Windows): the program in <root>\current, replaced on update; the data goes next to it in <root>
        var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var parent = Path.GetDirectoryName(exeDir);
        if (parent != null && Path.GetFileName(exeDir).Equals("current", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(parent, "Update.exe")))
            return Ensure(Path.Combine(parent, folder));
        return Ensure(Path.Combine(AppContext.BaseDirectory, folder));
    }

    /// <summary>Creates the folder; on Linux / macOS only the owner may enter it (the vault and keys live there).</summary>
    private static string Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return path;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path, ownerOnly);
        if (!existed && File.GetUnixFileMode(path) != ownerOnly) File.SetUnixFileMode(path, ownerOnly);
        return path;
    }

    /// <summary>How local paths compare: Linux file systems are case-sensitive, Windows and macOS ones (usually) not.</summary>
    public static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Path form that OpenSSH option parsing does not mangle (no backslash escapes).</summary>
    public static string ForSsh(string path) => path.Replace('\\', '/');
}
