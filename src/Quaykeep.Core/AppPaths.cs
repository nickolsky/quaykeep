namespace Quaykeep.Core;

/// <summary>
/// Locates the data folder. It lives in the Quaykeep folder (next to Quaykeep.sln),
/// so both a dev build under src\...\bin and a published copy under Quaykeep\app share it.
/// A side-by-side test build (instance.txt next to the exe, e.g. "dev") gets its own data folder (data-dev),
/// pipes and single-instance lock, so it runs next to the main copy without touching it.
/// </summary>
public static class AppPaths
{
    public const string SolutionMarker = "Quaykeep.sln";
    /// <summary>The solution file before the rename to Quaykeep: a checkout of that age keeps finding its data folder.</summary>
    public const string LegacySolutionMarker = "SSHManagement.sln";
    public const string InstanceFile = "instance.txt";

    /// <summary>Name of a side-by-side test instance, null for the main one.</summary>
    public static string? Instance { get; } = ResolveInstance();
    public static bool IsSideBySide => Instance != null;
    /// <summary>"" for the main instance, "-dev" for a test one: appended to pipe and lock names.</summary>
    public static string InstanceSuffix => Instance == null ? "" : "-" + Instance;
    /// <summary>"Quaykeep" or "Quaykeep [dev]".</summary>
    public static string ProductTitle => Instance == null ? "Quaykeep" : $"Quaykeep [{Instance}]";

    public static string DataDir { get; } = ResolveDataDir();
    public static string VaultFile => Path.Combine(DataDir, "vault.dat");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    public static string PublicKeyDir => Path.Combine(DataDir, "pub");

    public static string ExeDir => AppContext.BaseDirectory;
    public static string HelperExe => Path.Combine(ExeDir, "qk.exe");
    public static string MainExe => Path.Combine(ExeDir, "Quaykeep.exe");

    /// <summary>The standard OpenSSH agent pipe; a test instance never takes it.</summary>
    public static string AgentPipeDefault => Instance == null ? "openssh-ssh-agent" : AgentPipeFallback;
    /// <summary>
    /// The agent's own pipe. SSH Manager (the name before Quaykeep) told users to point SSH_AUTH_SOCK at
    /// "sshmanager-agent-&lt;user&gt;": while it still does, that name is kept so their ssh keeps working.
    /// </summary>
    public static string AgentPipeFallback => LegacyAgentPipeInUse ? LegacyAgentPipe : "quaykeep-agent-" + SafeUser + InstanceSuffix;
    public static string ControlPipe => "quaykeep-ctl-" + SafeUser + InstanceSuffix;
    // Kept from SSH Manager on purpose: an old copy and a new one must never run at once (same data folder and agent pipe).
    public static string SingleInstanceLock => @"Local\SshManager-" + Environment.UserName + InstanceSuffix;

    private static string LegacyAgentPipe => "sshmanager-agent-" + SafeUser + InstanceSuffix;

    private static bool LegacyAgentPipeInUse =>
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

    private static string ResolveDataDir()
    {
        var env = Env("DATA");
        if (!string.IsNullOrWhiteSpace(env))
            return Ensure(Path.GetFullPath(env));

        var folder = "data" + InstanceSuffix;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionMarker)) || File.Exists(Path.Combine(dir.FullName, LegacySolutionMarker)))
                return Ensure(Path.Combine(dir.FullName, folder));
            dir = dir.Parent;
        }
        return Ensure(Path.Combine(AppContext.BaseDirectory, folder));
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Path form that OpenSSH option parsing does not mangle (no backslash escapes).</summary>
    public static string ForSsh(string path) => path.Replace('\\', '/');
}
