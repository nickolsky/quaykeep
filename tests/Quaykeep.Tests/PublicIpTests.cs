using System.Text.Json;
using Quaykeep.Core.Models;
using Quaykeep.Core.Monitoring;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Storage;

namespace Quaykeep.Tests;

public class PublicIpTests
{
    [Fact]
    public void Legacy_servers_default_to_daily_detection_and_keep_their_ssh_host()
    {
        var server = JsonSerializer.Deserialize<ServerEntry>("""{"Host":"192.0.2.3"}""", VaultService.Json)!;
        Assert.True(server.AutoDetectPublicIps);
        Assert.Equal(1440, server.PublicIpIntervalMinutes);
        Assert.Null(server.PublicIpv4);
        Assert.Null(server.PublicIpv6);
        Assert.Equal("192.0.2.3", server.Host);
    }

    [Theory]
    [InlineData("@@sshm:ipv4\n198.51.100.2\n@@sshm:ipv6\n2001:DB8::2\n@@sshm:end", "198.51.100.2", "2001:db8::2")]
    [InlineData("@@sshm:ipv6\r\n2001:db8::2\r\n@@sshm:end", null, "2001:db8::2")]
    [InlineData("@@sshm:ipv4\n198.51.100.2\n@@sshm:ipv6\n198.51.100.2\n@@sshm:end", "198.51.100.2", null)]
    [InlineData("@@sshm:ipv4\n1.2.3\n@@sshm:ipv6\n::ffff:198.51.100.2\n@@sshm:end", null, null)]
    [InlineData("@@sshm:ipv4\n198.51.100.2", null, null)]
    public void Probe_results_are_parsed_by_family(string output, string? ipv4, string? ipv6)
    {
        var result = PublicIpMonitor.Parse(output);
        Assert.Equal((ipv4, ipv6), (result.Ipv4, result.Ipv6));
    }

    [Fact]
    public void Probe_reports_local_ipv6_even_when_public_ipv6_is_unreachable()
    {
        var result = PublicIpMonitor.Parse("""
            @@sshm:ipv4
            198.51.100.2
            @@sshm:ipv6-error
            7
            @@sshm:addr
            2: eth0 inet6 2001:db8::7/64 scope global
            2: eth0 inet6 fe80::1/64 scope link
            @@sshm:end
            """);
        Assert.Equal("198.51.100.2", result.Ipv4);
        Assert.Null(result.Ipv6);
        Assert.Equal(7, result.Ipv6CurlExitCode);
        Assert.Equal(["2001:db8::7/64"], result.InterfaceAddresses);
    }

    [Fact]
    public void Failed_families_keep_their_last_address_until_the_next_check()
    {
        var first = PublicIpObservation.Observe(DateTime.UtcNow, null, "198.51.100.1", "2001:db8::1");
        var second = PublicIpObservation.Observe(first.CheckedUtc.AddMinutes(1), first, null, "2001:db8::2");
        Assert.Equal("198.51.100.1", second.Ipv4);
        Assert.False(second.Ipv4Current);
        Assert.Equal("2001:db8::2", second.Ipv6);
        Assert.True(second.Ipv6Current);
        Assert.False(second.Due(second.CheckedUtc.AddSeconds(59), 1));
        Assert.True(second.Due(second.CheckedUtc.AddMinutes(1), 1));
        Assert.False(second.Due(second.CheckedUtc.AddHours(23), 1440));
        Assert.True(second.Due(second.CheckedUtc.AddDays(1), 1440));
    }

    [Fact]
    public void Copy_offers_the_ssh_address_and_each_available_public_family_separately()
    {
        var server = new ServerEntry
        {
            Host = "ssh.example.com",
            ManualPublicIpv4 = "198.51.100.1",
            Facts = new ServerFacts { PublicIps = PublicIpObservation.Observe(DateTime.UtcNow, null, null, "2001:db8::1") },
        };
        Assert.Equal([CopyKind.HostName, CopyKind.Ip, CopyKind.PublicIpv6, CopyKind.SshCommand],
            CopyTargets.For(server, false, new Dictionary<int, ServerHealth>()).Select(t => t.Kind));
        Assert.Equal("2001:db8::1", server.PublicIpv6);

        server.AutoDetectPublicIps = false;
        Assert.Equal([CopyKind.HostName, CopyKind.Ip, CopyKind.PublicIpv4, CopyKind.SshCommand],
            CopyTargets.For(server, false, new Dictionary<int, ServerHealth>()).Select(t => t.Kind));
        Assert.Equal("198.51.100.1", server.PublicIpv4);
        Assert.Null(server.PublicIpv6);
        Assert.Equal("ssh.example.com", server.Host);
    }
}
