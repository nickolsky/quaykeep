using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Tests;

public class TunnelTests
{
    private static ServerEntry Plain() => new() { Host = "vpn.example.com", Auth = AuthMode.Key };
    private static ServerEntry SsmShell() =>
        new() { Host = "i-0cac78034f134d1fc", Ssm = true, Auth = AuthMode.None, SsmProfile = "396517496207_AWSAdministratorAccess" };

    [Fact]
    public void Problems_Follow_The_Way_The_Server_Is_Reached()
    {
        var local = new Tunnel { LocalPort = 5432, RemoteHost = "10.0.1.20", RemotePort = 5432 };
        var socks = new Tunnel { Kind = TunnelKind.Dynamic, LocalPort = 1080 };
        Assert.Null(TunnelService.Problem(Plain(), local));
        Assert.Null(TunnelService.Problem(Plain(), socks));
        Assert.Equal(L.Get("Tunnel.NeedSsm"), TunnelService.Problem(Plain(), new Tunnel { Via = TunnelVia.Ssm, LocalPort = 1, RemotePort = 1 }));

        // an SSM shell without SSH login: only SSM port forwarding
        Assert.Equal(L.Get("Ssm.ShellOnly"), TunnelService.Problem(SsmShell(), local));
        Assert.Null(TunnelService.Problem(SsmShell(), new Tunnel { Via = TunnelVia.Ssm, LocalPort = 5432, RemoteHost = "10.0.1.20", RemotePort = 5432 }));
        Assert.Equal(L.Get("Tunnel.SsmNoSocks"), TunnelService.Problem(SsmShell(), new Tunnel { Via = TunnelVia.Ssm, Kind = TunnelKind.Dynamic, LocalPort = 1080 }));

        var keyed = SsmShell();
        keyed.Auth = AuthMode.Key;
        Assert.Null(TunnelService.Problem(keyed, socks));

        Assert.Equal(L.Get("Tunnel.BadLocalPort"), TunnelService.Problem(Plain(), new Tunnel { LocalPort = 0, RemotePort = 1 }));
        Assert.Equal(L.Get("Tunnel.BadRemotePort"), TunnelService.Problem(Plain(), new Tunnel { LocalPort = 1 }));
        Assert.Equal(L.Get("Tunnel.NoJump"), TunnelService.Problem(new ServerEntry { JumpHost = "bastion" }, socks));
    }

    [Fact]
    public void Ssm_Forwarding_Picks_The_Document_By_The_Host()
    {
        var s = SsmShell();
        var toInstance = AwsSsm.PortForwardArgs(s, null, 22, 15022);
        Assert.Equal("AWS-StartPortForwardingSession", toInstance[toInstance.IndexOf("--document-name") + 1]);
        var p = JsonNode.Parse(toInstance[toInstance.IndexOf("--parameters") + 1])!;
        Assert.Equal("22", (string?)p["portNumber"]![0]);
        Assert.Equal("15022", (string?)p["localPortNumber"]![0]);
        Assert.Null(p["host"]);

        var beyond = AwsSsm.PortForwardArgs(s, " 10.0.1.20 ", 5432, 5432);
        Assert.Equal("AWS-StartPortForwardingSessionToRemoteHost", beyond[beyond.IndexOf("--document-name") + 1]);
        Assert.Equal("10.0.1.20", (string?)JsonNode.Parse(beyond[beyond.IndexOf("--parameters") + 1])!["host"]![0]);
        Assert.Equal(["--profile", "396517496207_AWSAdministratorAccess"], beyond[^2..]);
    }

    [Fact]
    public void Ports_Are_Checked_On_This_Pc_And_Across_Servers()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            Assert.False(TunnelService.PortFree(((IPEndPoint)busy.LocalEndpoint).Port));
        }
        finally
        {
            busy.Stop();
        }

        var a = new Tunnel { LocalPort = 1080 };
        var data = new VaultData { Servers = [new ServerEntry { Tunnels = [a] }, new ServerEntry()] };
        Assert.Null(TunnelService.SamePort(data, a));
        Assert.Same(a, TunnelService.SamePort(data, new Tunnel { LocalPort = 1080 }));
        Assert.Equal("1080 → SOCKS", new Tunnel { Kind = TunnelKind.Dynamic, LocalPort = 1080 }.Route);
        Assert.Equal("5432 → localhost:5432", new Tunnel { LocalPort = 5432, RemotePort = 5432 }.Route);
    }
}
