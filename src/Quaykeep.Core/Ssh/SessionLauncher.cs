using System.Collections.Concurrent;
using System.Diagnostics;
using Quaykeep.Core.Agent;
using Quaykeep.Core.Crypto;
using Quaykeep.Core.Models;
using Quaykeep.Core.Pipes;
using Quaykeep.Core.Storage;

namespace Quaykeep.Core.Ssh;

/// <summary>
/// Builds ssh invocations and opens them in Windows Terminal or a console window (Windows), Terminal.app (macOS) or
/// the desktop's terminal emulator (Linux).
/// Secrets never appear on command lines: the terminal runs "qk tab &lt;token&gt;", which fetches
/// the real command from this process, and passwords are handed to ssh via SSH_ASKPASS on request.
/// </summary>
public sealed class SessionLauncher(VaultService vault, SettingsService settings, SshAgentServer agent, KnownHostsService knownHosts)
{
    private static readonly TimeSpan LaunchTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AskPassTtl = TimeSpan.FromMinutes(10);
    private const int MaxPasswordAnswers = 2;

    private readonly ConcurrentDictionary<string, (LaunchSpec Spec, DateTime Expires)> _launches = new();
    private readonly ConcurrentDictionary<string, AskPassTicket> _askPass = new();

    private sealed class AskPassTicket
    {
        public Guid ServerId;
        public DateTime Expires;
        public int Answers;
    }

    public string SshPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(settings.Settings.SshPath)) return settings.Settings.SshPath!;
            if (!OperatingSystem.IsWindows()) return FindOnPath("ssh") ?? "/usr/bin/ssh";
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            return File.Exists(system) ? system : "ssh.exe";
        }
    }

    public static string? FindWindowsTerminal()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(alias) ? alias : FindOnPath("wt.exe");
    }

    /// <summary>A program in PATH, or null.</summary>
    public static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), name);
                if (dir.Trim().Length > 0 && File.Exists(p)) return p;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Linux: the terminal emulator to open sessions in and how it takes a command to run: $TERMINAL first, then the
    /// Debian alternative, then the usual desktop terminals. Null when none is installed.
    /// </summary>
    public static (string Program, string[] Before)? FindLinuxTerminal()
    {
        var known = new (string Name, string[] Before)[]
        {
            ("x-terminal-emulator", ["-e"]), ("gnome-terminal", ["--"]), ("konsole", ["-e"]), ("xfce4-terminal", ["-x"]),
            ("mate-terminal", ["-x"]), ("tilix", ["-e"]), ("kitty", []), ("alacritty", ["-e"]), ("wezterm", ["start", "--"]),
            ("foot", []), ("xterm", ["-e"]),
        };
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } preferred && FindOnPath(preferred) is { } own)
            return (own, known.FirstOrDefault(k => k.Name == Path.GetFileName(preferred)).Before ?? ["-e"]);
        foreach (var (name, before) in known)
            if (FindOnPath(name) is { } path) return (path, before);
        return null;
    }

    /// <summary>What "outside the window" opens on this computer (Settings shows it).</summary>
    public static string ExternalTerminalName =>
        OperatingSystem.IsWindows() ? FindWindowsTerminal() != null ? "Windows Terminal" : "console"
        : OperatingSystem.IsMacOS() ? "Terminal"
        : FindLinuxTerminal() is { } t ? Path.GetFileName(t.Program) : "";

    /// <param name="remoteCommand">Command to run instead of a login shell (gets a TTY via -t).</param>
    public LaunchSpec BuildSpec(ServerEntry server, bool pauseOnError, string? remoteCommand = null, string? title = null)
    {
        if (!server.SshLogin) return SsmShellSpec(server, pauseOnError, remoteCommand, title);
        var s = settings.Settings;
        var args = new List<string>
        {
            "-p", server.Port.ToString(),
            "-o", "UserKnownHostsFile=" + AppPaths.ForSsh(knownHosts.FilePath),
        };
        if (s.ServerAliveInterval > 0) args.AddRange(["-o", $"ServerAliveInterval={s.ServerAliveInterval}"]);
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) args.AddRange(["-J", server.JumpHost.Trim()]);
        if (server.Ssm)
        {
            var cli = AwsSsm.FindCli() ?? throw new InvalidOperationException(L.Get("Ssm.NoCli"));
            args.AddRange(["-o", "ProxyCommand=" + AwsSsm.ProxyCommand(server, cli)]);
        }

        var env = new Dictionary<string, string>();
        if (agent.PipePath != null) env["SSH_AUTH_SOCK"] = agent.PipePath;

        if (server.Auth == AuthMode.Key)
        {
            var key = server.KeyId is { } id ? vault.Data.Keys.FirstOrDefault(k => k.Id == id) : null;
            if (key == null) throw new InvalidOperationException(L.F("Launch.NoKey", server.Name));
            args.AddRange(["-o", "IdentitiesOnly=yes", "-i", AppPaths.ForSsh(KeyService.EnsurePublicKeyFile(key))]);
        }
        else
        {
            if (string.IsNullOrEmpty(server.Password))
                throw new InvalidOperationException(L.F("Launch.NoPassword", server.Name));
            var token = NewToken();
            _askPass[token] = new AskPassTicket { ServerId = server.Id, Expires = DateTime.UtcNow + AskPassTtl };
            env["SSH_ASKPASS"] = AppPaths.HelperExe;
            env["SSH_ASKPASS_REQUIRE"] = "force";
            env["SSHM_ASKPASS_TOKEN"] = token;
            args.AddRange([
                "-o", "PubkeyAuthentication=no",
                "-o", "PreferredAuthentications=keyboard-interactive,password",
                "-o", "NumberOfPasswordPrompts=1",
            ]);
        }

        args.AddRange(SplitArgs(server.ExtraArgs));
        if (remoteCommand != null) args.Add("-t");
        args.AddRange(["-l", server.Username, server.Host]);
        if (remoteCommand != null) args.Add(remoteCommand);

        var name = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name;
        return new LaunchSpec
        {
            SshPath = SshPath,
            Args = args,
            Env = env,
            Title = title == null ? name : $"{name}: {title}",
            PauseOnError = pauseOnError,
            PauseAlways = remoteCommand != null,
        };
    }

    /// <summary>
    /// The plain "aws ssm start-session" shell of a server without SSH login: no ssh, no keys, nothing left on the server.
    /// Commands need an SSH login, so <paramref name="remoteCommand"/> is refused.
    /// </summary>
    private static LaunchSpec SsmShellSpec(ServerEntry server, bool pauseOnError, string? remoteCommand, string? title)
    {
        if (remoteCommand != null) throw new InvalidOperationException(L.Get("Ssm.ShellOnly"));
        var psi = AwsSsm.ShellStartInfo(server);
        var name = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name;
        return new LaunchSpec
        {
            SshPath = psi.FileName,
            Args = [.. psi.ArgumentList],
            Env = psi.Environment.TryGetValue("PATH", out var path) && path != Environment.GetEnvironmentVariable("PATH")
                ? new Dictionary<string, string> { ["PATH"] = path! }
                : [],
            Title = title == null ? name : $"{name}: {title}",
            PauseOnError = pauseOnError,
        };
    }

    /// <summary>Opens a session (or runs <paramref name="remoteCommand"/>) in a new terminal tab/window.</summary>
    public void Launch(ServerEntry server, string? remoteCommand = null, string? title = null)
    {
        var mode = settings.Settings.Terminal;
        var wt = mode == TerminalMode.ConsoleWindow ? null : FindWindowsTerminal();
        // a terminal that closes when ssh ends would take an error message with it
        var spec = BuildSpec(server, pauseOnError: wt == null, remoteCommand, title);
        var token = NewToken();
        _launches[token] = (spec, DateTime.UtcNow + LaunchTtl);
        Cleanup();

        var (helper, helperArgs) = AppPaths.Helper;
        ProcessStartInfo psi;
        if (OperatingSystem.IsMacOS())
        {
            // Terminal.app runs a command line: the helper and the one-time token (no secrets)
            var line = string.Join(' ', new[] { helper }.Concat(helperArgs).Concat(["tab", token]).Select(RemoteShell.Quote));
            psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
            foreach (var a in new[]
                     {
                         "-e", "tell application \"Terminal\"", "-e", "activate",
                         "-e", $"do script \"{line.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"", "-e", "end tell",
                     })
                psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            MarkConnected(server);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            var terminal = FindLinuxTerminal() ?? throw new InvalidOperationException(L.Get("Launch.NoTerminal"));
            psi = new ProcessStartInfo(terminal.Program) { UseShellExecute = false };
            foreach (var a in terminal.Before) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(helper);
            foreach (var a in helperArgs) psi.ArgumentList.Add(a);
        }
        else if (wt != null)
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(mode == TerminalMode.WindowsTerminalWindow ? "new" : "0");
            psi.ArgumentList.Add("new-tab");
            if (!string.IsNullOrWhiteSpace(settings.Settings.WindowsTerminalProfile))
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(settings.Settings.WindowsTerminalProfile!);
            }
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(spec.Title.Replace(';', ','));
            psi.ArgumentList.Add("--suppressApplicationTitle");
            psi.ArgumentList.Add(AppPaths.HelperExe);
        }
        else
        {
            // GUI parent without a console: the console helper gets its own new console window.
            psi = new ProcessStartInfo(AppPaths.HelperExe) { UseShellExecute = false };
        }
        psi.ArgumentList.Add("tab");
        psi.ArgumentList.Add(token);
        Process.Start(psi)?.Dispose();
        MarkConnected(server);
    }

    private void MarkConnected(ServerEntry server)
    {
        vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == server.Id);
            if (s != null) s.LastConnected = DateTime.Now;
        });
    }

    public LaunchSpec? TakeLaunch(string token)
    {
        Cleanup();
        return _launches.TryRemove(token, out var e) && e.Expires > DateTime.UtcNow ? e.Spec : null;
    }

    /// <summary>Returns the stored password for an askpass request, or null if the ticket is invalid/used up.</summary>
    public string? AnswerAskPass(string token)
    {
        if (!_askPass.TryGetValue(token, out var t) || t.Expires < DateTime.UtcNow) return null;
        if (Interlocked.Increment(ref t.Answers) > MaxPasswordAnswers) return null;
        return vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == t.ServerId)?.Password, out var pw) ? pw : null;
    }

    /// <summary>Plain command for copy/paste (no secrets).</summary>
    public string CommandLine(ServerEntry server)
    {
        if (!server.SshLogin) return AwsSsm.ShellCommandLine(server, "aws");
        var parts = new List<string> { "ssh" };
        if (server.Port != 22) parts.AddRange(["-p", server.Port.ToString()]);
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) parts.AddRange(["-J", server.JumpHost.Trim()]);
        if (server.Ssm) parts.AddRange(["-o", RemoteShell.Quote("ProxyCommand=" + AwsSsm.ProxyCommand(server, "aws"))]);
        if (!string.IsNullOrWhiteSpace(server.ExtraArgs)) parts.Add(server.ExtraArgs.Trim());
        parts.Add($"{server.Username}@{server.Host}");
        return string.Join(' ', parts);
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (k, v) in _launches)
            if (v.Expires < now) _launches.TryRemove(k, out _);
        foreach (var (k, v) in _askPass)
            if (v.Expires < now) _askPass.TryRemove(k, out _);
    }

    private static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public static IEnumerable<string> SplitArgs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var cur = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in s)
        {
            if (quote != null)
            {
                if (c == quote) quote = null;
                else cur.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) yield return cur.ToString();
                cur.Clear();
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }
}
