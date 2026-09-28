using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Quaykeep.Core.Monitoring;

/// <param name="RoundtripMs">Set when <see cref="Status"/> is Success.</param>
/// <param name="Error">Why the ping could not be sent at all (no route, ICMP not allowed…).</param>
public sealed record PingReplyInfo(int Seq, IPStatus Status, long? RoundtripMs, int? Ttl, string? Error = null)
{
    public bool Ok => Status == IPStatus.Success;
}

/// <summary>Running totals of a ping series, like the summary of ping.exe.</summary>
public sealed class PingStats
{
    private long _sum;

    public int Sent { get; private set; }
    public int Received { get; private set; }
    public long? Min { get; private set; }
    public long? Max { get; private set; }
    public long? Avg => Received == 0 ? null : _sum / Received;
    public int LossPercent => Sent == 0 ? 0 : (int)Math.Round(100.0 * (Sent - Received) / Sent);

    public void Add(PingReplyInfo r)
    {
        Sent++;
        if (!r.Ok || r.RoundtripMs is not { } ms) return;
        Received++;
        _sum += ms;
        Min = Min is { } min ? Math.Min(min, ms) : ms;
        Max = Max is { } max ? Math.Max(max, ms) : ms;
    }
}

/// <summary>
/// ICMP echo from this PC ("Ping…" in a server's menu). Availability monitoring does not use it (see
/// <see cref="HealthMonitor"/>): VPN hosts often drop ICMP, so this is a manual diagnostic only.
/// </summary>
public static class PingService
{
    private static readonly byte[] Payload = new byte[32];

    /// <summary>The host itself when it is an IP; otherwise its DNS address, IPv4 first.</summary>
    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct = default)
    {
        host = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host, ct);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>One echo request every <paramref name="interval"/> until cancelled (or <paramref name="count"/> sent).</summary>
    public static async IAsyncEnumerable<PingReplyInfo> PingAsync(IPAddress address, TimeSpan interval, TimeSpan timeout,
        int? count = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var ping = new Ping();
        for (var seq = 1; count is not { } n || seq <= n; seq++)
        {
            if (ct.IsCancellationRequested) yield break;
            var started = Stopwatch.StartNew();
            var reply = await SendAsync(ping, address, seq, timeout, ct);
            if (reply == null) yield break;
            yield return reply;
            if (count is { } last && seq >= last) yield break;
            var wait = interval - started.Elapsed;
            if (wait > TimeSpan.Zero && !await DelayAsync(wait, ct)) yield break;
        }
    }

    /// <returns>null when cancelled.</returns>
    private static async Task<PingReplyInfo?> SendAsync(Ping ping, IPAddress address, int seq, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var r = await ping.SendPingAsync(address, timeout, Payload, null, ct);
            return new PingReplyInfo(seq, r.Status, r.Status == IPStatus.Success ? r.RoundtripTime : null, r.Options?.Ttl);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (PingException ex)
        {
            return new PingReplyInfo(seq, IPStatus.Unknown, null, null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await Task.Delay(wait, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
