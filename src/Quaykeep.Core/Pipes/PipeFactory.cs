using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Quaykeep.Core.Pipes;

internal static class PipeFactory
{
    /// <summary>
    /// Named pipe instance accessible only to the current user. Windows: an ACL with only this user. Linux / macOS: .NET
    /// makes a Unix socket (the name itself when it is a path, else CoreFxPipe_&lt;name&gt; in the temp folder) that
    /// CurrentUserOnly limits to this user.
    /// </summary>
    public static NamedPipeServerStream Create(string name, bool first)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (first) RemoveStaleSocket(name);
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        return CreateWindows(name, first);
    }

    /// <summary>The socket file of a pipe on Linux / macOS.</summary>
    public static string SocketPath(string name) => Path.IsPathRooted(name) ? name : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);

    /// <summary>
    /// A socket file left by a process that died blocks the name. One that nobody answers on is removed; one that answers
    /// belongs to a running copy, and creating the pipe then fails as it should.
    /// </summary>
    private static void RemoveStaleSocket(string name)
    {
        var path = SocketPath(name);
        if (!File.Exists(path)) return;
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return;
        }
        throw new IOException($"{path} is in use");
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindows(string name, bool first)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(me);
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }
}

/// <summary>Accept loop: one server instance waiting at a time, each client handled on its own task.</summary>
public abstract class PipeServerBase : IDisposable
{
    private CancellationTokenSource? _cts;

    public string? PipeName { get; private set; }
    public bool IsRunning => PipeName != null;

    /// <summary>Tries each candidate name; returns false if all are taken by another process.</summary>
    protected bool Start(params string[] candidates)
    {
        foreach (var name in candidates)
        {
            NamedPipeServerStream first;
            try
            {
                first = PipeFactory.Create(name, first: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            PipeName = name;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(first, _cts.Token);
            return true;
        }
        return false;
    }

    private async Task AcceptLoop(NamedPipeServerStream server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                await server.DisposeAsync();
                server = PipeFactory.Create(PipeName!, first: false);
                continue;
            }

            var client = server;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(client, ct);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                               or FormatException or EndOfStreamException)
                {
                    // client went away or sent garbage
                }
                finally
                {
                    await client.DisposeAsync();
                }
            }, ct);

            server = PipeFactory.Create(PipeName!, first: false);
        }
    }

    protected abstract Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct);

    public void Dispose()
    {
        _cts?.Cancel();
        PipeName = null;
    }
}
