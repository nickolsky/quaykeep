using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Core.Monitoring;

/// <summary>Traceroute from this PC with the system's tool (Linux / macOS), read line by line into hops.</summary>
public static partial class TraceRoute
{
    /// <summary>traceroute / traceroute6 / tracepath and whether its output is tracepath's.</summary>
    internal sealed record LocalTool(string Path, bool Tracepath);

    private static LocalTool? SystemTool(IPAddress target)
    {
        var v6 = target.AddressFamily == AddressFamily.InterNetworkV6;
        // macOS: traceroute takes IPv4 only, traceroute6 IPv6
        string[] names = OperatingSystem.IsMacOS() && v6 ? ["traceroute6"] : ["traceroute"];
        foreach (var name in names)
            if (Find(name) is { } p) return new LocalTool(p, false);
        return OperatingSystem.IsLinux() && Find("tracepath") is { } tp ? new LocalTool(tp, true) : null;
    }

    private static string? Find(string name) =>
        SessionLauncher.FindOnPath(name) ?? new[] { "/usr/sbin", "/usr/bin", "/sbin", "/bin" }.Select(d => System.IO.Path.Combine(d, name)).FirstOrDefault(File.Exists);

    internal static List<string> SystemArguments(LocalTool tool, IPAddress target, TimeSpan timeout, int maxHops) => tool.Tracepath
        ? ["-n", "-m", maxHops.ToString(), target.ToString()]
        : ["-n", "-q", Probes.ToString(), "-w", Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(), "-m", maxHops.ToString(), target.ToString()];

    private static async IAsyncEnumerable<TraceHop> RunSystemAsync(LocalTool tool, IPAddress target, TimeSpan timeout, int maxHops,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool.Path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in SystemArguments(tool, target, timeout, maxHops)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(tool.Path);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None); // the header line ("traceroute to …") goes there on macOS
        var reader = new LocalHopReader(tool.Tracepath, target.ToString());
        var silent = 0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                foreach (var hop in reader.Add(line))
                {
                    yield return hop;
                    silent = hop.Silent ? silent + 1 : 0;
                    if (hop.Reached || silent >= GiveUpAfter) yield break;
                }
            }
            foreach (var hop in reader.Finish()) yield return hop;
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }
    }
}

/// <summary>
/// Turns the output of traceroute -n (one line per hop) or tracepath -n (one line per probe) into hops as soon as each
/// hop is complete; the hop whose router is the destination is marked reached.
/// </summary>
public sealed class LocalHopReader(bool tracepath, string target)
{
    private readonly List<string> _pending = [];
    private int? _pendingNumber;

    public IEnumerable<TraceHop> Add(string line)
    {
        if (!tracepath)
        {
            foreach (var hop in TraceRoute.ParseTraceroute(line)) yield return Mark(hop);
            yield break;
        }
        var parsed = TraceRoute.ParseTracepath(line);
        if (parsed.Count == 0) yield break;
        var n = parsed[0].Number;
        if (_pendingNumber is { } p && p != n)
            foreach (var hop in Flush()) yield return hop;
        _pendingNumber = n;
        _pending.Add(line);
    }

    /// <summary>The last tracepath hop, at the end of the output.</summary>
    public IEnumerable<TraceHop> Finish() => Flush();

    private IEnumerable<TraceHop> Flush()
    {
        var hops = TraceRoute.ParseTracepath(string.Join("\n", _pending)).Select(Mark).ToList();
        _pending.Clear();
        _pendingNumber = null;
        return hops;
    }

    private TraceHop Mark(TraceHop hop)
    {
        if (hop.Address == target) hop.Reached = true;
        return hop;
    }
}
