using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Core.Monitoring;

/// <summary>One step of a route: the router that answered (null = no answer) and the round trips of the probes.</summary>
public sealed class TraceHop
{
    public int Number { get; init; }
    public string? Address { get; set; }
    /// <summary>One entry per probe, null for a probe that got no answer.</summary>
    public List<double?> Rtts { get; init; } = [];
    /// <summary>The destination itself answered.</summary>
    public bool Reached { get; set; }
    public string? HostName { get; set; }

    public double? Best => Rtts.Where(r => r.HasValue).Select(r => r!.Value).Order().Cast<double?>().FirstOrDefault();
    public bool Silent => Address == null;
}

/// <summary>
/// Traceroute: from this PC with ICMP echoes of growing TTL (what tracert.exe does), or on a server over SSH with
/// traceroute (tracepath when it is missing), whose output is parsed into the same hops.
/// </summary>
public static partial class TraceRoute
{
    public const int MaxHops = 30;
    private const int Probes = 3;
    /// <summary>Hops in a row without an answer after which the destination is taken to drop the probes.</summary>
    private const int GiveUpAfter = 6;
    private static readonly byte[] Payload = new byte[32];

    public static async IAsyncEnumerable<TraceHop> RunAsync(IPAddress target, TimeSpan timeout, int maxHops = MaxHops,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var silent = 0;
        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            if (ct.IsCancellationRequested) yield break;
            var replies = await Task.WhenAll(Enumerable.Range(0, Probes).Select(_ => ProbeAsync(target, ttl, timeout, ct)));
            if (ct.IsCancellationRequested) yield break;
            var hop = new TraceHop { Number = ttl };
            foreach (var r in replies)
            {
                if (r is { Status: IPStatus.Success or IPStatus.TtlExpired } && r.Address is { } a)
                {
                    hop.Address ??= a.ToString();
                    hop.Rtts.Add(r.RoundtripTime);
                    if (r.Status == IPStatus.Success) hop.Reached = true;
                }
                else hop.Rtts.Add(null);
            }
            yield return hop;
            if (hop.Reached) yield break;
            silent = hop.Silent ? silent + 1 : 0;
            if (silent >= GiveUpAfter) yield break;
        }
    }

    private static async Task<PingReply?> ProbeAsync(IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            return await ping.SendPingAsync(target, timeout, Payload, new PingOptions(ttl, dontFragment: true), ct);
        }
        catch (Exception ex) when (ex is PingException or OperationCanceledException)
        {
            return null;
        }
    }

    // ---------- on a server ----------

    [GeneratedRegex(@"^[A-Za-z0-9.:\-]{1,253}$")]
    private static partial Regex TargetRegex();

    /// <summary>
    /// Runs traceroute on the server (installing it when missing and root), or tracepath. Output after a marker line
    /// that tells which one ran.
    /// </summary>
    public static string RemoteScript(string target)
    {
        if (!TargetRegex().IsMatch(target)) throw new ArgumentException(L.F("Trace.BadTarget", target));
        return $$"""
            t={{RemoteShell.Quote(target)}}
            if ! command -v traceroute >/dev/null 2>&1 && [ "$(id -u)" = 0 ]; then
              if command -v apt-get >/dev/null 2>&1; then
                DEBIAN_FRONTEND=noninteractive apt-get install -y -q traceroute >/dev/null 2>&1 ||
                  { apt-get update -q >/dev/null 2>&1; DEBIAN_FRONTEND=noninteractive apt-get install -y -q traceroute >/dev/null 2>&1; }
              elif command -v dnf >/dev/null 2>&1; then dnf install -y -q traceroute >/dev/null 2>&1
              elif command -v yum >/dev/null 2>&1; then yum install -y -q traceroute >/dev/null 2>&1
              fi
            fi
            if command -v traceroute >/dev/null 2>&1; then echo '@@sshm:traceroute'; traceroute -n -q 3 -w 2 -m {{MaxHops}} "$t" 2>&1
            elif command -v tracepath >/dev/null 2>&1; then echo '@@sshm:tracepath'; tracepath -n -m {{MaxHops}} "$t" 2>&1
            else echo '@@sshm:none'
            fi
            """;
    }

    /// <summary>The hops from <see cref="RemoteScript"/>'s output; null when neither tool is on the server.</summary>
    public static List<TraceHop>? ParseRemote(string output, string target)
    {
        var i = output.IndexOf("@@sshm:", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = output[i..];
        var nl = rest.IndexOf('\n');
        var marker = (nl < 0 ? rest : rest[..nl]).Trim();
        var body = nl < 0 ? "" : rest[(nl + 1)..];
        var hops = marker switch
        {
            "@@sshm:traceroute" => ParseTraceroute(body),
            "@@sshm:tracepath" => ParseTracepath(body),
            _ => null,
        };
        if (hops is { Count: > 0 } && hops[^1].Address == target) hops[^1].Reached = true;
        return hops;
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(.*)$")]
    private static partial Regex HopLine();

    /// <summary>
    /// traceroute -n: " 3  10.0.0.1  1.234 ms 10.0.0.2  1.5 ms *" (a probe may be answered by another router; the
    /// first one is kept), " 4  * * *", "!H"-style flags ignored.
    /// </summary>
    public static List<TraceHop> ParseTraceroute(string text)
    {
        var hops = new List<TraceHop>();
        foreach (var raw in text.Split('\n'))
        {
            var m = HopLine().Match(raw);
            if (!m.Success) continue;
            var hop = new TraceHop { Number = int.Parse(m.Groups[1].Value) };
            var t = m.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var k = 0; k < t.Length; k++)
            {
                if (t[k] == "*") hop.Rtts.Add(null);
                else if (k + 1 < t.Length && t[k + 1] == "ms" &&
                         double.TryParse(t[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
                    hop.Rtts.Add(ms);
                else if (IsAddress(t[k])) hop.Address ??= t[k];
            }
            hops.Add(hop);
        }
        return hops;
    }

    /// <summary>A full address: IPAddress also takes "5.1" (= 5.0.0.1), which is a time here.</summary>
    private static bool IsAddress(string s) => (s.Contains(':') || s.Count(c => c == '.') == 3) && IPAddress.TryParse(s, out _);

    [GeneratedRegex(@"^\s*(\d+)\??:\s+(\S+)(?:\s+([\d.]+)ms)?(.*)$")]
    private static partial Regex TracepathLine();

    /// <summary>tracepath -n: " 1:  172.17.0.1  0.083ms", " 2:  no reply", " 3:  1.1.1.1  5.2ms reached"; one line per probe.</summary>
    public static List<TraceHop> ParseTracepath(string text)
    {
        var hops = new SortedDictionary<int, TraceHop>();
        foreach (var raw in text.Split('\n'))
        {
            var m = TracepathLine().Match(raw);
            if (!m.Success || m.Groups[2].Value.StartsWith('[')) continue; // [LOCALHOST]
            var n = int.Parse(m.Groups[1].Value);
            if (!hops.TryGetValue(n, out var hop)) hops[n] = hop = new TraceHop { Number = n };
            if (m.Groups[2].Value == "no")
            {
                hop.Rtts.Add(null);
                continue;
            }
            if (IsAddress(m.Groups[2].Value)) hop.Address ??= m.Groups[2].Value;
            hop.Rtts.Add(double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null);
            if (m.Groups[4].Value.Contains("reached")) hop.Reached = true;
        }
        return [.. hops.Values];
    }
}
