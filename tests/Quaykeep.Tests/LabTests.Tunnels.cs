using System.Net.Sockets;
using System.Text;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

public partial class LabTests
{
    /// <summary>
    /// ssh -L and ssh -D through <see cref="TunnelService"/>: a local port to the server's own sshd answers with its banner,
    /// a SOCKS5 CONNECT through the dynamic port reaches it too, and stopping frees both ports.
    /// </summary>
    [Fact]
    public async Task Local_And_Socks_Tunnels_Carry_Connections_And_Stop()
    {
        if (!Enabled("tunnels")) return;
        await OnEachServer(async lab =>
        {
            // a background tunnel never asks about host keys: the first, interactive connection stores it
            lab.Ssh.Connect(lab.Server).Dispose();
            // clear of the ports the lab publishes (18001-18024, see compose.yml)
            var basePort = 19500 + (lab.Server.Port - 2200) * 10;
            var local = new Tunnel { LocalPort = basePort, RemotePort = 22 };
            var socks = new Tunnel { Kind = TunnelKind.Dynamic, LocalPort = basePort + 1 };
            lab.Vault.Update(d => d.Servers.First(s => s.Id == lab.Server.Id).Tunnels.AddRange([local, socks]));
            using var tunnels = new TunnelService(lab.Vault, lab.Ssh);
            foreach (var t in new[] { local, socks })
            {
                tunnels.Start(lab.Server.Id, t.Id);
                for (var i = 0; i < 60 && tunnels.Get(t.Id).State != TunnelState.Running; i++) await Task.Delay(250);
                Assert.True(tunnels.Get(t.Id).State == TunnelState.Running, $"{t.Route}: {tunnels.Get(t.Id)}");
            }

            using (var c = new TcpClient())
            {
                await c.ConnectAsync("127.0.0.1", local.LocalPort);
                Assert.StartsWith("SSH-2.0-", await ReadLine(c.GetStream()));
            }
            using (var c = new TcpClient())
            {
                await c.ConnectAsync("127.0.0.1", socks.LocalPort);
                var s = c.GetStream();
                await s.WriteAsync(new byte[] { 5, 1, 0 }); // SOCKS5, no authentication
                var hello = new byte[2];
                await s.ReadExactlyAsync(hello);
                Assert.Equal(new byte[] { 5, 0 }, hello);
                await s.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 22 }); // CONNECT 127.0.0.1:22 as the server sees it
                var reply = new byte[10];
                await s.ReadExactlyAsync(reply);
                Assert.Equal(0, reply[1]);
                Assert.StartsWith("SSH-2.0-", await ReadLine(s));
            }

            tunnels.Stop(local.Id);
            tunnels.Stop(socks.Id);
            for (var i = 0; i < 40 && (tunnels.Get(local.Id).State != TunnelState.Stopped || tunnels.Get(socks.Id).State != TunnelState.Stopped); i++)
                await Task.Delay(250);
            Assert.Equal(TunnelState.Stopped, tunnels.Get(local.Id).State);
            Assert.True(TunnelService.PortFree(local.LocalPort) && TunnelService.PortFree(socks.LocalPort));
            log.WriteLine($"{lab.Server.Name}: {local.Route} and {socks.Route} worked");
        });
    }

    private static async Task<string> ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await s.ReadAsync(b, cts.Token) == 1 && b[0] != '\n') sb.Append((char)b[0]);
        return sb.ToString().Trim();
    }
}
