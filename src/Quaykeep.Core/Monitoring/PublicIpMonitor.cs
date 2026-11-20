using System.Net.Sockets;
using Quaykeep.Core.Inventory;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Storage;
using Renci.SshNet.Common;

namespace Quaykeep.Core.Monitoring;

public sealed record PublicIpChange(string ServerName, string Previous, string Current);
public sealed record PublicIpProbeResult(string? Ipv4 = null, string? Ipv6 = null, int? Ipv4CurlExitCode = null,
    int? Ipv6CurlExitCode = null, PublicIpCheckIssue Issue = PublicIpCheckIssue.None, List<string>? InterfaceAddresses = null);

/// <summary>Checks one trusted server at a time and keeps each family's last successful result.</summary>
public sealed class PublicIpMonitor(VaultService vault, SshClientFactory ssh, KnownHostsService knownHosts)
{
    private const string Probe = """
        lookup() {
          if address=$(curl -q "$2" -fsS --noproxy '*' --connect-timeout 3 --max-time 6 --max-filesize 256 "$3" 2>/dev/null); then
            printf '@@sshm:%s\n%s\n' "$1" "$address"
          else
            printf '@@sshm:%s-error\n%s\n' "$1" "$?"
          fi
        }
        if command -v curl >/dev/null 2>&1; then
          lookup ipv4 -4 https://api.ipify.org
          lookup ipv6 -6 https://api6.ipify.org
        else
          printf '@@sshm:curl-missing\n'
        fi
        if addresses=$(ip -o addr show 2>/dev/null); then
          printf '@@sshm:addr\n%s\n' "$addresses"
        fi
        printf '@@sshm:end\n'
        """;

    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _sweep = new(1);
    public event EventHandler<PublicIpChange>? AddressChanged;

    public static bool Supported(ServerEntry server) =>
        ServerInventoryService.Supported(server) && string.IsNullOrWhiteSpace(server.ExtraArgs);

    public static bool SameEndpoint(ServerEntry a, ServerEntry b) =>
        (a.Host, a.Port, a.Username, a.Ssm, a.SsmProfile, a.SsmRegion, a.JumpHost, a.ExtraArgs)
        == (b.Host, b.Port, b.Username, b.Ssm, b.SsmProfile, b.SsmRegion, b.JumpHost, b.ExtraArgs);

    public Task CheckDueAsync(CancellationToken token) => Task.Run(async () =>
    {
        if (!await _sweep.WaitAsync(0)) return;
        try
        {
            var now = DateTime.UtcNow;
            if (!vault.TryRead(d => d.Servers.Where(s => s.AutoDetectPublicIps && Supported(s) && IsDue(s, now))
                    .Select(s => s.Id).ToArray(), out var ids)) return;
            foreach (var id in ids)
            {
                await _gate.WaitAsync(token);
                try { Check(id, requestedAt: null, token); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _sweep.Release();
        }
    });

    private static bool IsDue(ServerEntry server, DateTime now) =>
        server.Facts?.PublicIps?.Due(now, server.PublicIpIntervalMinutes) != false;

    public Task CheckNowAsync(Guid id, CancellationToken token)
    {
        var requestedAt = DateTime.UtcNow;
        return Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(token);
                try { Check(id, requestedAt, token); }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        });
    }

    private void Check(Guid id, DateTime? requestedAt, CancellationToken token)
    {
        if (token.IsCancellationRequested ||
            !vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == id)?.Clone(), out var server) ||
            server == null || !server.AutoDetectPublicIps || !Supported(server)) return;

        var before = server.Facts?.PublicIps;
        if (requestedAt is { } since ? before?.CheckedUtc >= since : !IsDue(server, DateTime.UtcNow)) return;

        PublicIpProbeResult probe;
        try
        {
            if (!knownHosts.IsKnown(server.Host, server.Port))
            {
                if (requestedAt == null) return;
                probe = new(Issue: PublicIpCheckIssue.HostKey);
            }
            else
            {
                using var client = ssh.Connect(server, interactive: false);
                if (token.IsCancellationRequested) return;
                var result = RemoteShell.Run(client, server, Probe, elevated: false, timeout: TimeSpan.FromSeconds(15));
                probe = result.Ok ? Parse(result.Output) : new(Issue: PublicIpCheckIssue.Incomplete);
            }
        }
        catch (HostKeyNotTrustedException)
        {
            probe = new(Issue: PublicIpCheckIssue.HostKey);
        }
        catch (Exception ex) when (ex is SshException or SocketException or TimeoutException or
            IOException or UnauthorizedAccessException or InvalidOperationException or VaultLockedException or
            System.ComponentModel.Win32Exception)
        {
            probe = new(Issue: PublicIpCheckIssue.Ssh);
        }

        var resultAt = PublicIpObservation.Observe(DateTime.UtcNow, before, probe.Ipv4, probe.Ipv6) with
        {
            Issue = probe.Issue,
            Ipv4CurlExitCode = probe.Ipv4CurlExitCode,
            Ipv6CurlExitCode = probe.Ipv6CurlExitCode,
        };
        try
        {
            if (!vault.UpdateFacts(id, f =>
                {
                    f.PublicIps = resultAt;
                    if (probe.InterfaceAddresses != null) f.Addresses = probe.InterfaceAddresses;
                }, current => !token.IsCancellationRequested &&
                    current.AutoDetectPublicIps && Supported(current) && SameEndpoint(current, server) &&
                    current.Facts?.PublicIps == before)) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        ReportChange(server.Name, before?.Ipv4, probe.Ipv4);
        ReportChange(server.Name, before?.Ipv6, probe.Ipv6);
    }

    private void ReportChange(string name, string? previous, string? address)
    {
        if (previous != null && address != null && previous != address)
            AddressChanged?.Invoke(this, new PublicIpChange(name, previous, address));
    }

    public static PublicIpProbeResult Parse(string output)
    {
        var sections = SectionParser.Split(output);
        if (!sections.ContainsKey("end")) return new(Issue: PublicIpCheckIssue.Incomplete);
        int? ExitCode(string key) => int.TryParse(sections.GetValueOrDefault(key), out var code) ? code : null;
        return new(
            PublicIpObservation.Normalize(sections.GetValueOrDefault("ipv4"), AddressFamily.InterNetwork),
            PublicIpObservation.Normalize(sections.GetValueOrDefault("ipv6"), AddressFamily.InterNetworkV6),
            ExitCode("ipv4-error"), ExitCode("ipv6-error"),
            sections.ContainsKey("curl-missing") ? PublicIpCheckIssue.CurlMissing : PublicIpCheckIssue.None,
            sections.TryGetValue("addr", out var addresses) ? InterfaceAddressParser.Parse(addresses) : null);
    }
}
