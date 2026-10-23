using System.Net;
using Quaykeep.Core.Monitoring;

namespace Quaykeep.Tests;

/// <summary>Traceroute from this PC on Linux / macOS: the system tool's output read line by line.</summary>
public class TraceRouteSystemTests
{
    [Fact]
    public void Traceroute_Lines_Become_Hops_As_They_Come()
    {
        var reader = new LocalHopReader(tracepath: false, "1.1.1.1");
        // macOS / Linux traceroute -n -q 3: the header goes to stderr on macOS, to stdout on Linux
        Assert.Empty(reader.Add("traceroute to 1.1.1.1 (1.1.1.1), 30 hops max, 60 byte packets"));
        var h1 = Assert.Single(reader.Add(" 1  192.168.1.1  0.512 ms  0.433 ms  0.401 ms"));
        Assert.Equal(1, h1.Number);
        Assert.Equal("192.168.1.1", h1.Address);
        Assert.Equal([0.512, 0.433, 0.401], h1.Rtts);
        var h2 = Assert.Single(reader.Add(" 2  * * *"));
        Assert.True(h2.Silent);
        Assert.Equal(3, h2.Rtts.Count);
        var h3 = Assert.Single(reader.Add(" 3  10.20.0.1  5.1 ms * 10.20.0.2  6.0 ms"));
        Assert.Equal("10.20.0.1", h3.Address);
        Assert.Equal([5.1, null, 6.0], h3.Rtts);
        Assert.False(h3.Reached);
        var last = Assert.Single(reader.Add(" 4  1.1.1.1  9.870 ms  9.6 ms  9.9 ms"));
        Assert.True(last.Reached);
        Assert.Empty(reader.Finish());
    }

    [Fact]
    public void Tracepath_Probes_Are_Grouped_Per_Hop()
    {
        var reader = new LocalHopReader(tracepath: true, "1.1.1.1");
        Assert.Empty(reader.Add(" 1?: [LOCALHOST]                      pmtu 1500"));
        Assert.Empty(reader.Add(" 1:  192.168.1.1                                           0.623ms"));
        Assert.Empty(reader.Add(" 1:  192.168.1.1                                           0.511ms"));
        var h1 = Assert.Single(reader.Add(" 2:  no reply"));
        Assert.Equal("192.168.1.1", h1.Address);
        Assert.Equal(2, h1.Rtts.Count);
        var h2 = Assert.Single(reader.Add(" 3:  1.1.1.1                                               9.512ms reached"));
        Assert.True(h2.Silent);
        Assert.Empty(reader.Add("     Resume: pmtu 1500 hops 3 back 3 "));
        var h3 = Assert.Single(reader.Finish());
        Assert.Equal(3, h3.Number);
        Assert.True(h3.Reached);
        Assert.Equal([9.512], h3.Rtts);
    }

    [Fact]
    public void Arguments_Carry_Timeout_Hops_And_Numeric_Output()
    {
        var args = TraceRoute.SystemArguments(new TraceRoute.LocalTool("/usr/sbin/traceroute", false), IPAddress.Parse("1.1.1.1"),
            TimeSpan.FromSeconds(1.5), 30);
        Assert.Equal(["-n", "-q", "3", "-w", "2", "-m", "30", "1.1.1.1"], args);
        var tp = TraceRoute.SystemArguments(new TraceRoute.LocalTool("/usr/bin/tracepath", true), IPAddress.Parse("::1"), TimeSpan.FromSeconds(2), 20);
        Assert.Equal(["-n", "-m", "20", "::1"], tp);
    }
}
