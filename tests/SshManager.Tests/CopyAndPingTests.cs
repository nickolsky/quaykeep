using System.Net;
using System.Net.NetworkInformation;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Ssh;

namespace SshManager.Tests;

public class CopyTargetTests
{
    private static readonly Dictionary<int, ServerHealth> NoPorts = [];

    private static List<CopyKind> Kinds(ServerEntry s, bool hasKey = false, IReadOnlyDictionary<int, ServerHealth>? ports = null) =>
        CopyTargets.For(s, hasKey, ports ?? NoPorts).Select(t => t.Kind).ToList();

    [Fact]
    public void Password_And_Key_Only_When_There_Is_One()
    {
        var s = new ServerEntry { Host = "vpn.example.com", Username = "root" };
        Assert.Equal([CopyKind.HostName, CopyKind.Ip, CopyKind.SshCommand], Kinds(s));
        s.Password = "secret";
        Assert.Equal([CopyKind.HostName, CopyKind.Ip, CopyKind.Password, CopyKind.PublicKey, CopyKind.SshCommand], Kinds(s, hasKey: true));
    }

    [Fact]
    public void Web_Addresses_Follow_Open_Ports()
    {
        var s = new ServerEntry
        {
            Host = "site.example.com",
            Facts = new ServerFacts
            {
                ListeningPorts =
                [
                    new ListeningPort { Port = 443, Addresses = "0.0.0.0" },
                    new ListeningPort { Port = 8080, Addresses = "127.0.0.1", LocalOnly = true },
                    new ListeningPort { Port = 80, Addresses = "0.0.0.0" },
                ],
            },
        };
        Assert.Equal(["http://site.example.com", "https://site.example.com"],
            CopyTargets.For(s, false, NoPorts).Where(t => t.Kind == CopyKind.Url).Select(t => t.Url));

        // a monitored port's last check wins over the inventory
        var ports = new Dictionary<int, ServerHealth>
        {
            [80] = new(HealthState.Offline),
            [8443] = new(HealthState.Online),
            [8080] = new(HealthState.Unknown),
        };
        Assert.Equal(["https://site.example.com", "https://site.example.com:8443"],
            CopyTargets.For(s, false, ports).Where(t => t.Kind == CopyKind.Url).Select(t => t.Url));
    }

    [Theory]
    [InlineData("1.2.3.4", 80, "http", "http://1.2.3.4")]
    [InlineData("1.2.3.4", 8443, "https", "https://1.2.3.4:8443")]
    [InlineData("2001:db8::1", 443, "https", "https://[2001:db8::1]")]
    [InlineData("[2001:db8::1]", 8080, "http", "http://[2001:db8::1]:8080")]
    public void Web_Url(string host, int port, string scheme, string expected) =>
        Assert.Equal(expected, CopyTargets.WebUrl(host, port, scheme));

    [Fact]
    public async Task Ip_And_Host_Name_Resolve()
    {
        Assert.True(CopyTargets.IsIp("10.0.0.1"));
        Assert.True(CopyTargets.IsIp("[::1]"));
        Assert.False(CopyTargets.IsIp("vpn.example.com"));
        Assert.Equal("10.0.0.1", await CopyTargets.ResolveIpAsync(" 10.0.0.1 "));
        Assert.Equal("localhost", await CopyTargets.ResolveHostNameAsync("localhost"));
        Assert.True(IPAddress.TryParse(await CopyTargets.ResolveIpAsync("localhost"), out _));
    }
}

public class PingTests
{
    [Fact]
    public void Stats_Add_Up()
    {
        var stats = new PingStats();
        stats.Add(new PingReplyInfo(1, IPStatus.Success, 10, 64));
        stats.Add(new PingReplyInfo(2, IPStatus.TimedOut, null, null));
        stats.Add(new PingReplyInfo(3, IPStatus.Success, 30, 64));
        stats.Add(new PingReplyInfo(4, IPStatus.Success, 20, 64));
        Assert.Equal(4, stats.Sent);
        Assert.Equal(3, stats.Received);
        Assert.Equal(25, stats.LossPercent);
        Assert.Equal(10, stats.Min);
        Assert.Equal(20, stats.Avg);
        Assert.Equal(30, stats.Max);
        Assert.Equal(0, new PingStats().LossPercent);
        Assert.Null(new PingStats().Avg);
    }

    [Fact]
    public async Task Loopback_Answers()
    {
        var address = await PingService.ResolveAsync("127.0.0.1");
        var replies = new List<PingReplyInfo>();
        await foreach (var r in PingService.PingAsync(address, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), count: 3))
            replies.Add(r);
        Assert.Equal([1, 2, 3], replies.Select(r => r.Seq));
        Assert.All(replies, r => Assert.True(r.Ok, r.Error ?? r.Status.ToString()));
    }

    [Fact]
    public async Task Stops_When_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        var n = 0;
        await foreach (var _ in PingService.PingAsync(IPAddress.Loopback, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), ct: cts.Token))
        {
            n++;
            cts.Cancel();
        }
        Assert.Equal(1, n);
    }
}

public class PasswordLoginScriptTests
{
    [Fact]
    public void Scripts_Are_Posix_Sh()
    {
        Bash.CheckSyntax(KeySetupService.DisablePasswordScript, posix: true);
        Bash.CheckSyntax(KeySetupService.RestorePasswordScript, posix: true);
        Assert.DoesNotContain("[[ ", KeySetupService.DisablePasswordScript); // RemoteShell runs sh, not bash
    }

    [Fact]
    public void Disable_Checks_Before_Reloading_And_Undo_Removes_Everything()
    {
        var s = KeySetupService.DisablePasswordScript;
        Assert.Contains("00-sshm-no-password.conf", s);
        Assert.True(s.IndexOf("\"$SSHD\" -t", StringComparison.Ordinal) < s.IndexOf("reload_sshd ||", StringComparison.Ordinal));
        Assert.Contains("[ \"$(effective)\" = no ]", s);
        Assert.Contains("authenticationmethods", s);
        var r = KeySetupService.RestorePasswordScript;
        Assert.Contains("undo", r);
        Assert.Contains("reload_sshd", r);
    }
}
