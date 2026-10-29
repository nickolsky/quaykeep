using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Ssh;

/// <summary>
/// SSH over AWS Systems Manager. The CLI's AWS-StartSSHSession document carries the SSH bytes on its stdin/stdout, as
/// ssh's ProxyCommand would: the login itself stays ordinary SSH, SSM is only the pipe.
/// </summary>
public static class AwsSsm
{
    public const string Document = "AWS-StartSSHSession";

    /// <summary>The AWS CLI: PATH first, then where the installers put it (a macOS app doesn't get the shell's PATH).</summary>
    public static string? FindCli()
    {
        if (OperatingSystem.IsWindows())
            return SessionLauncher.FindOnPath("aws.exe") ?? SessionLauncher.FindOnPath("aws.cmd")
                ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "AWSCLIV2", "aws.exe"));
        return SessionLauncher.FindOnPath("aws") ?? Existing("/usr/local/bin/aws", "/opt/homebrew/bin/aws", "/usr/bin/aws");
    }

    /// <summary>The Session Manager plugin the CLI runs; the CLI itself only looks in PATH.</summary>
    public static string? FindPlugin()
    {
        if (OperatingSystem.IsWindows())
            return SessionLauncher.FindOnPath("session-manager-plugin.exe")
                ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon",
                    "SessionManagerPlugin", "bin", "session-manager-plugin.exe"));
        return SessionLauncher.FindOnPath("session-manager-plugin") ?? Existing("/usr/local/bin/session-manager-plugin",
            "/usr/local/sessionmanagerplugin/bin/session-manager-plugin", "/opt/homebrew/bin/session-manager-plugin",
            "/usr/bin/session-manager-plugin");
    }

    private static string? Existing(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>Arguments of "aws ssm start-session" that tunnel to sshd's <paramref name="port"/> on <paramref name="target"/>.</summary>
    public static List<string> StartSessionArgs(ServerEntry server, string target, string port) =>
        WithProfile(server, ["ssm", "start-session", "--target", target, "--document-name", Document, "--parameters", "portNumber=" + port]);

    /// <summary>The plain shell session (as ssm-user, no SSH): what a server without SSH login opens.</summary>
    public static List<string> ShellArgs(ServerEntry server) =>
        WithProfile(server, ["ssm", "start-session", "--target", server.Host.Trim()]);

    /// <summary>Arguments that run <paramref name="command"/> through AWS-StartNonInteractiveCommand.</summary>
    public static List<string> CommandArgs(ServerEntry server, string command) =>
        WithProfile(server, ["ssm", "start-session", "--target", server.Host.Trim(), "--document-name", "AWS-StartNonInteractiveCommand",
            // JSON, not the CLI's shorthand: a command has commas and quotes
            "--parameters", new JsonObject { ["command"] = new JsonArray(command) }.ToJsonString()]);

    private static List<string> WithProfile(ServerEntry server, List<string> a)
    {
        if (!string.IsNullOrWhiteSpace(server.SsmProfile)) a.AddRange(["--profile", server.SsmProfile.Trim()]);
        if (!string.IsNullOrWhiteSpace(server.SsmRegion)) a.AddRange(["--region", server.SsmRegion.Trim()]);
        return a;
    }

    /// <summary>
    /// SSM port forwarding without SSH: <paramref name="localPort"/> on this PC to <paramref name="remotePort"/> on the
    /// instance, or on <paramref name="remoteHost"/> as the instance reaches it.
    /// </summary>
    public static List<string> PortForwardArgs(ServerEntry server, string? remoteHost, int remotePort, int localPort)
    {
        var p = new JsonObject
        {
            ["portNumber"] = new JsonArray(remotePort.ToString()),
            ["localPortNumber"] = new JsonArray(localPort.ToString()),
        };
        var document = "AWS-StartPortForwardingSession";
        if (!string.IsNullOrWhiteSpace(remoteHost))
        {
            p["host"] = new JsonArray(remoteHost.Trim());
            document = "AWS-StartPortForwardingSessionToRemoteHost";
        }
        return WithProfile(server, ["ssm", "start-session", "--target", server.Host.Trim(), "--document-name", document,
            "--parameters", p.ToJsonString()]);
    }

    public static ProcessStartInfo PortForwardStartInfo(ServerEntry server, string? remoteHost, int remotePort, int localPort) =>
        Cli(PortForwardArgs(server, remoteHost, remotePort, localPort));

    public static string ShellCommandLine(ServerEntry server, string cli) =>
        string.Join(' ', new[] { cli }.Concat(ShellArgs(server)).Select(QuoteArg));

    /// <summary>The same session as an OpenSSH ProxyCommand (the external terminal and the copied command).</summary>
    public static string ProxyCommand(ServerEntry server, string cli) =>
        string.Join(' ', new[] { cli }.Concat(StartSessionArgs(server, server.Host.Trim(), server.Port.ToString())).Select(QuoteArg));

    /// <summary>Double quotes around anything but plain words: ProxyCommand is split by a shell on Unix, by ssh itself on Windows.</summary>
    private static string QuoteArg(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@,".Contains(c)) ? s : "\"" + s + "\"";

    /// <summary>Starts the CLI for a <see cref="SsmTunnel"/>, with the plugin's folder in PATH.</summary>
    public static ProcessStartInfo StartInfo(ServerEntry server) =>
        Cli(StartSessionArgs(server, server.Host.Trim(), server.Port.ToString()));

    /// <summary>The plain shell (<see cref="ShellArgs"/>) for a terminal.</summary>
    public static ProcessStartInfo ShellStartInfo(ServerEntry server) => Cli(ShellArgs(server));

    private static ProcessStartInfo Cli(IEnumerable<string> args)
    {
        var cli = FindCli() ?? throw new InvalidOperationException(L.Get("Ssm.NoCli"));
        var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // the CLI finds the plugin only in PATH, which a macOS app doesn't inherit from the shell
        if (FindPlugin() is { } plugin && SessionLauncher.FindOnPath(Path.GetFileName(plugin)) == null)
            psi.Environment["PATH"] = Path.GetDirectoryName(plugin) + Path.PathSeparator + psi.Environment["PATH"];
        return psi;
    }

    /// <summary>
    /// Runs a POSIX sh script on the instance through SSM, as the session's user (ssm-user, which has sudo, unless the
    /// account set another Run As user). The plugin's own "Starting/Exiting session" lines are part of the output.
    /// </summary>
    public static async Task<(string Output, string Errors)> RunAsync(ServerEntry server, string script, TimeSpan timeout)
    {
        // a CR would reach sh as part of every line (a checkout with CRLF puts them into raw strings), as in RemoteShell.Run
        var psi = Cli(CommandArgs(server, "sh -c " + RemoteShell.Quote(script.Replace("\r\n", "\n"))));
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(psi.FileName);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException(L.Get("Ssm.CommandTimeout"));
        }
        return (await output, await errors);
    }

    /// <summary>
    /// Adds <paramref name="publicKey"/> to <paramref name="user"/>'s authorized_keys (sudo when the session isn't root),
    /// creating ~/.ssh with the user's ownership. Prints SSHM_OK, or SSHM_NOUSER when there is no such user.
    /// </summary>
    public static string InstallKeyScript(string user, string publicKey) =>
        $"u={RemoteShell.Quote(user)}; pub={RemoteShell.Quote(publicKey)}\n" + InstallKeyBody.ReplaceLineEndings("\n");

    private const string InstallKeyBody = """
        h=$(getent passwd "$u" | cut -d: -f6)
        if [ -z "$h" ]; then echo SSHM_NOUSER; exit 1; fi
        if [ "$(id -u)" = 0 ]; then S=; else S='sudo -n'; fi
        $S sh -c 'umask 077; d="$1/.ssh"
          mkdir -p "$d" && touch "$d/authorized_keys" &&
          { grep -qxF "$2" "$d/authorized_keys" || echo "$2" >> "$d/authorized_keys"; } &&
          chown "$3": "$d" "$d/authorized_keys" && chmod 700 "$d" && chmod 600 "$d/authorized_keys"' sh "$h" "$pub" "$u" && echo SSHM_OK
        """;

    /// <summary>
    /// "aws sso login" for the server's profile: the CLI opens the browser and waits for the sign-in there.
    /// Returns null on success, otherwise what went wrong.
    /// </summary>
    public static async Task<string?> SsoLoginAsync(ServerEntry server, TimeSpan timeout)
    {
        var cli = FindCli() ?? throw new InvalidOperationException(L.Get("Ssm.NoCli"));
        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "sso", "login" }) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(server.SsmProfile))
        {
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add(server.SsmProfile.Trim());
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(cli);
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return L.Get("Ssm.SignInTimeout");
        }
        if (p.ExitCode == 0) return null;
        var text = (await errors).Trim();
        return text.Length > 0 ? text : (await output).Trim();
    }

    /// <summary>What the CLI's error output means for the user, or null when it said nothing.</summary>
    public static string? Explain(string? errors, ServerEntry server)
    {
        if (string.IsNullOrWhiteSpace(errors)) return null;
        var profile = string.IsNullOrWhiteSpace(server.SsmProfile) ? "default" : server.SsmProfile.Trim();
        if (errors.Contains("SessionManagerPlugin is not found", StringComparison.OrdinalIgnoreCase)) return L.Get("Ssm.NoPlugin");
        if (errors.Contains("sso", StringComparison.OrdinalIgnoreCase) &&
            (errors.Contains("expired", StringComparison.OrdinalIgnoreCase) || errors.Contains("sso login", StringComparison.OrdinalIgnoreCase)
             || errors.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
            return L.F("Ssm.SsoExpired", profile);
        return L.F("Ssm.Failed", errors.Trim());
    }
}

/// <summary>
/// A loopback port that SSH.NET connects to, piped into one "aws ssm start-session" process. It accepts a single
/// connection and stops listening, and the process ends with that connection (and with this program).
/// </summary>
public sealed class SsmTunnel : IDisposable
{
    private const int KeptErrorLines = 20;
    private static readonly ConcurrentDictionary<SsmTunnel, byte> Live = new();

    static SsmTunnel()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var t in Live.Keys) t.Dispose();
        };
    }

    private readonly TcpListener _listener;
    private readonly ProcessStartInfo _psi;
    private readonly Queue<string> _errors = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Socket? _socket;
    private int _disposed;

    public int Port { get; }

    public SsmTunnel(ProcessStartInfo psi)
    {
        _psi = psi;
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Live[this] = 0;
        _ = RunAsync();
    }

    /// <summary>The process's last error lines once it ended (or after <paramref name="wait"/>).</summary>
    public string WaitErrors(TimeSpan wait)
    {
        _exited.Task.Wait(wait);
        lock (_errors) return string.Join('\n', _errors);
    }

    private async Task RunAsync()
    {
        try
        {
            _socket = await _listener.AcceptSocketAsync();
            _listener.Stop();
            var process = _process = Process.Start(_psi) ?? throw new InvalidOperationException(_psi.FileName);
            if (Volatile.Read(ref _disposed) != 0)
            {
                process.Kill(entireProcessTree: true);
                return;
            }
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_errors)
                {
                    _errors.Enqueue(e.Data.Trim());
                    while (_errors.Count > KeptErrorLines) _errors.Dequeue();
                }
            };
            process.BeginErrorReadLine();
            using var net = new NetworkStream(_socket, ownsSocket: false);
            var up = PumpAsync(net, process.StandardInput.BaseStream);
            var down = PumpAsync(process.StandardOutput.BaseStream, net);
            await Task.WhenAny(up, down);
            if (down.IsCompleted) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or TimeoutException or IOException
                                      or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (Volatile.Read(ref _disposed) == 0)
                lock (_errors) _errors.Enqueue(e.Message);
        }
        finally
        {
            Dispose();
        }
    }

    private static async Task PumpAsync(Stream from, Stream to)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                await to.FlushAsync();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Live.TryRemove(this, out _);
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }
        try
        {
            // a FIN first: closing with the read still pending would reset the connection
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
        _socket?.Dispose();
        if (_process is { } p)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            p.Dispose();
        }
        _exited.TrySetResult();
    }
}
