using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Storage;

namespace Quaykeep.Core.Forwarding;

public enum TunnelState { Stopped, Starting, Running, Retrying, Failed }

public sealed record TunnelStatus(TunnelState State, string? Error = null);

/// <summary>Stops a tunnel for good: retrying would not help (a busy port, an expired SSO sign-in, an untrusted host key).</summary>
public sealed class TunnelFatalException(string message) : Exception(message);

/// <summary>
/// Runs the servers' <see cref="Tunnel"/>s while Quaykeep runs: SSH.NET forwarded ports (ssh -L / -D, also over SSM)
/// or an "aws ssm start-session" port forwarding process. Listens on loopback only. A dropped tunnel comes back with
/// growing pauses; the vault being locked only delays that (connecting needs the server's key or password).
/// </summary>
public sealed class TunnelService(VaultService vault, SshClientFactory ssh) : IDisposable
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];
    private static readonly TimeSpan Stable = TimeSpan.FromMinutes(1);
    private const int KeptErrorLines = 10;

    private sealed class Runner
    {
        public readonly CancellationTokenSource Cts = new();
        public Task Task = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<Guid, Runner> _runners = new();
    private readonly ConcurrentDictionary<Guid, TunnelStatus> _status = new();

    /// <summary>A tunnel's state changed (raised on a background thread).</summary>
    public event EventHandler<Guid>? StateChanged;

    public TunnelStatus Get(Guid tunnelId) => _status.TryGetValue(tunnelId, out var s) ? s : new TunnelStatus(TunnelState.Stopped);

    /// <summary>Started and not stopped (running, connecting or waiting to retry).</summary>
    public bool IsOn(Guid tunnelId) => _runners.TryGetValue(tunnelId, out var r) && !r.Cts.IsCancellationRequested;

    /// <summary>Why the tunnel can't work with this server, or null.</summary>
    public static string? Problem(ServerEntry server, Tunnel t)
    {
        if (t.LocalPort is < 1 or > 65535) return L.Get("Tunnel.BadLocalPort");
        if (t.Kind == TunnelKind.Local && t.RemotePort is < 1 or > 65535) return L.Get("Tunnel.BadRemotePort");
        if (t.Via == TunnelVia.Ssm)
        {
            if (!server.Ssm) return L.Get("Tunnel.NeedSsm");
            if (t.Kind == TunnelKind.Dynamic) return L.Get("Tunnel.SsmNoSocks");
            return null;
        }
        if (!server.SshLogin) return L.Get("Ssm.ShellOnly");
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) return L.Get("Tunnel.NoJump");
        return null;
    }

    /// <summary>Another tunnel (of any server) on the same local port.</summary>
    public static Tunnel? SamePort(VaultData data, Tunnel t) =>
        data.Servers.SelectMany(s => s.Tunnels).FirstOrDefault(o => o.Id != t.Id && o.LocalPort == t.LocalPort);

    public static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Start(Guid serverId, Guid tunnelId)
    {
        if (_runners.TryGetValue(tunnelId, out var old))
        {
            if (!old.Cts.IsCancellationRequested) return;
            // still closing: start once the port is released
            old.Task.ContinueWith(_ => Start(serverId, tunnelId), TaskScheduler.Default);
            return;
        }
        var runner = new Runner();
        if (!_runners.TryAdd(tunnelId, runner)) return;
        Set(tunnelId, new TunnelStatus(TunnelState.Starting));
        runner.Task = Task.Run(() => RunAsync(serverId, tunnelId, runner));
    }

    public void Stop(Guid tunnelId)
    {
        if (_runners.TryGetValue(tunnelId, out var r)) r.Cts.Cancel();
    }

    /// <summary>The tunnels marked "start with Quaykeep" (after unlocking).</summary>
    public void StartAutostart()
    {
        if (!vault.TryRead(d => d.Servers.SelectMany(s => s.Tunnels.Where(t => t.AutoStart).Select(t => (s.Id, t.Id))).ToList(), out var list))
            return;
        foreach (var (server, tunnel) in list) Start(server, tunnel);
    }

    private void Set(Guid id, TunnelStatus status)
    {
        _status[id] = status;
        StateChanged?.Invoke(this, id);
    }

    private async Task RunAsync(Guid serverId, Guid tunnelId, Runner runner)
    {
        var ct = runner.Cts.Token;
        var attempt = 0;
        string? failed = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // read again every time: the tunnel may have been edited, the vault locked or unlocked
                if (!vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == serverId) is { } s && s.Tunnels.FirstOrDefault(t => t.Id == tunnelId) is { } t
                        ? (s.Clone(), t.Clone())
                        : default, out var found))
                {
                    Set(tunnelId, new TunnelStatus(TunnelState.Retrying, L.Get("Vault.Locked")));
                    await Task.Delay(Backoff[^1], ct);
                    continue;
                }
                var (server, tunnel) = found;
                if (server == null || tunnel == null) return; // deleted
                if (Problem(server, tunnel) is { } problem) throw new TunnelFatalException(problem);
                if (!PortFree(tunnel.LocalPort))
                {
                    if (attempt == 0) throw new TunnelFatalException(L.F("Tunnel.PortBusy", tunnel.LocalPort));
                }
                else
                {
                    var started = DateTime.UtcNow;
                    string? error;
                    try
                    {
                        error = tunnel.Via == TunnelVia.Ssm
                            ? await RunSsmAsync(server, tunnel, ct)
                            : await RunSshAsync(server, tunnel, ct);
                    }
                    catch (HostKeyNotTrustedException ex)
                    {
                        throw new TunnelFatalException(ex.Message + " " + L.Get("Tunnel.ConnectOnce"));
                    }
                    catch (Exception ex) when (ex is not TunnelFatalException and not OperationCanceledException)
                    {
                        error = ex.Message;
                    }
                    if (AwsProblem(server, error)) throw new TunnelFatalException(error!);
                    if (ct.IsCancellationRequested) return;
                    if (DateTime.UtcNow - started > Stable) attempt = 0;
                    Set(tunnelId, new TunnelStatus(TunnelState.Retrying, error));
                }
                await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)], ct);
                attempt++;
            }
        }
        catch (TunnelFatalException ex)
        {
            failed = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // stopped, deleted or failed: whichever way the loop ended
            _runners.TryRemove(new KeyValuePair<Guid, Runner>(tunnelId, runner));
            Set(tunnelId, failed == null ? new TunnelStatus(TunnelState.Stopped) : new TunnelStatus(TunnelState.Failed, failed));
        }
    }

    /// <summary>One SSH connection with one forwarded port; returns when it drops (the reason) or is stopped (null).</summary>
    private async Task<string?> RunSshAsync(ServerEntry server, Tunnel tunnel, CancellationToken ct)
    {
        using var client = ssh.Connect(server, interactive: false);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var ended = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorOccurred += (_, e) => ended.TrySetResult(e.Exception.Message);
        ForwardedPort port = tunnel.Kind == TunnelKind.Dynamic
            ? new ForwardedPortDynamic(IPAddress.Loopback.ToString(), (uint)tunnel.LocalPort)
            : new ForwardedPortLocal(IPAddress.Loopback.ToString(), (uint)tunnel.LocalPort,
                string.IsNullOrWhiteSpace(tunnel.RemoteHost) ? "localhost" : tunnel.RemoteHost.Trim(), (uint)tunnel.RemotePort);
        // a refused connection to the far end fails that one channel, not the tunnel
        port.Exception += (_, _) => { };
        client.AddForwardedPort(port);
        try
        {
            port.Start();
        }
        catch (SocketException)
        {
            throw new TunnelFatalException(L.F("Tunnel.PortBusy", tunnel.LocalPort));
        }
        Set(tunnel.Id, new TunnelStatus(TunnelState.Running));
        using (ct.Register(() => ended.TrySetResult(null)))
        {
            while (!ended.Task.IsCompleted)
            {
                await Task.WhenAny(ended.Task, Task.Delay(2000, CancellationToken.None));
                if (!client.IsConnected) ended.TrySetResult(L.Get("Term.ConnectionLost"));
            }
        }
        try
        {
            port.Stop();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or SocketException)
        {
        }
        return await ended.Task;
    }

    /// <summary>One "aws ssm start-session" port forwarding process; returns when it exits (the reason) or is stopped (null).</summary>
    private async Task<string?> RunSsmAsync(ServerEntry server, Tunnel tunnel, CancellationToken ct)
    {
        var psi = AwsSsm.PortForwardStartInfo(server, tunnel.RemoteHost, tunnel.RemotePort, tunnel.LocalPort);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(psi.FileName);
        var lines = new Queue<string>();
        void Keep(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (lines)
            {
                lines.Enqueue(line.Trim());
                while (lines.Count > KeptErrorLines) lines.Dequeue();
            }
        }
        p.OutputDataReceived += (_, e) =>
        {
            // the plugin prints this once it listens on the local port
            if (e.Data?.Contains("Waiting for connections", StringComparison.OrdinalIgnoreCase) == true)
                Set(tunnel.Id, new TunnelStatus(TunnelState.Running));
            else if (e.Data?.Contains("Starting session", StringComparison.OrdinalIgnoreCase) == false) Keep(e.Data);
        };
        p.ErrorDataReceived += (_, e) => Keep(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            return null;
        }
        string text;
        lock (lines) text = string.Join('\n', lines);
        return AwsSsm.Explain(text, server) ?? L.F("Term.ExitCode", p.ExitCode);
    }

    /// <summary>An AWS answer that retrying won't change: no CLI or plugin, an expired SSO sign-in (until "Sign in to AWS").</summary>
    private static bool AwsProblem(ServerEntry server, string? error) =>
        server.Ssm && error != null &&
        (error == L.Get("Ssm.NoCli") || error == L.Get("Ssm.NoPlugin") ||
         error == L.F("Ssm.SsoExpired", string.IsNullOrWhiteSpace(server.SsmProfile) ? "default" : server.SsmProfile.Trim()));

    public void Dispose()
    {
        var runners = _runners.Values.ToList();
        foreach (var r in runners) r.Cts.Cancel();
        Task.WaitAll(runners.Select(r => r.Task).ToArray(), TimeSpan.FromSeconds(3));
    }
}
