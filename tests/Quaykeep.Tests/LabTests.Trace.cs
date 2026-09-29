using Quaykeep.Core.Monitoring;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Tests;

public partial class LabTests
{
    /// <summary>
    /// Traceroute from the server: installed when missing (the CentOS image has neither traceroute nor tracepath), run to
    /// an address on the internet, and parsed into hops; the first one is the lab's Docker gateway.
    /// </summary>
    [Fact]
    public async Task Traceroute_From_The_Server_Installs_Runs_And_Parses()
    {
        if (!Enabled("trace")) return;
        await OnEachServer(lab =>
        {
            using var client = lab.Ssh.Connect(lab.Server);
            var r = RemoteShell.Run(client, lab.Server, TraceRoute.RemoteScript("1.1.1.1"), elevated: true, TimeSpan.FromMinutes(4));
            var hops = TraceRoute.ParseRemote(r.Output, "1.1.1.1");
            Assert.True(hops != null, r.Combined);
            log.WriteLine($"{lab.Server.Name}: {r.Output.Split('\n').FirstOrDefault(l => l.StartsWith("@@sshm:"))}\n" +
                          string.Join("\n", hops!.Select(h => $"  {h.Number} {h.Address ?? "*"} {h.Best}{(h.Reached ? " reached" : "")}")));
            Assert.NotEmpty(hops);
            Assert.Equal(1, hops[0].Number);
            Assert.NotNull(hops[0].Address);
            Assert.Contains("@@sshm:traceroute", r.Output); // installed where it was missing
            return Task.CompletedTask;
        });
    }
}
