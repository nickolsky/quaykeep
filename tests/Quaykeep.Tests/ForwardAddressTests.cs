using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

public class ForwardAddressTests
{
    [Fact]
    public void Targets_include_both_families_and_keep_outbound_addresses_out_of_the_listen_choices()
    {
        var server = new ServerEntry
        {
            Host = "ssh.example.test",
            Facts = new ServerFacts
            {
                Addresses = ["10.0.0.2/24", "2001:DB8::2/64", "2001:db8::2/64", "fe80::2/64"],
                PublicIps = new PublicIpObservation(DateTime.UtcNow, "198.51.100.2", "2001:db8::7", true, false),
            },
        };
        Assert.Equal([
            new ForwardAddress("ssh.example.test", ForwardAddressSource.Ssh),
            new ForwardAddress("10.0.0.2", ForwardAddressSource.Interface),
            new ForwardAddress("2001:db8::2", ForwardAddressSource.Interface),
            new ForwardAddress("198.51.100.2", ForwardAddressSource.PublicOutbound),
            new ForwardAddress("2001:db8::7", ForwardAddressSource.LastKnownPublicOutbound),
        ], ForwardAddresses.Targets(server));
        Assert.Equal(["0.0.0.0", "::", "10.0.0.2", "2001:db8::2"], ForwardAddresses.Listen(server));

        server.AutoDetectPublicIps = false;
        server.ManualPublicIpv6 = "2001:db8::9";
        var manual = ForwardAddresses.Targets(server);
        Assert.DoesNotContain(manual, a => a.Address == "2001:db8::7");
        Assert.Contains(new ForwardAddress("2001:db8::9", ForwardAddressSource.PublicOutbound), manual);
    }
}
