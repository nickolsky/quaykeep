using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

/// <summary>
/// The firewall on the lab servers. They see this PC as their Docker gateway (the SSH client address), so rules name
/// that address; a published container port is reached from here through the lab's port mapping (18001 → 8080…).
/// </summary>
public partial class LabTests
{
    /// <summary>The port on this PC that compose.yml maps to 8080 on the lab server (by its SSH port), 0 when unknown.</summary>
    private static int WebPort(ServerEntry s) => s.Port switch { 2201 => 18001, 2202 => 18011, 2205 => 18021, _ => 0 };

    private static async Task<bool> Answers(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            return (await http.GetAsync($"http://127.0.0.1:{port}/")).IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static FirewallRule FwRule(FirewallAction a, string ports, params string[] sources) =>
        new() { Action = a, Ports = ports, Protocol = "tcp", Sources = [.. sources] };

    [Fact]
    public async Task Firewall_Blocks_Allows_Reverts_And_Survives_A_Restart()
    {
        if (!Enabled("firewall")) return;
        await OnEachServer(async lab =>
        {
            var server = lab.Server.Clone();
            var fw = new FirewallService(lab.Vault, lab.Ssh);
            var lines = new List<string>();
            void Log(string l) { lock (lines) lines.Add(l); }
            fw.TurnOff(server, Log);
            var me = fw.ClientAddress(server)!.ToString();
            log.WriteLine($"{server.Name}: this PC is {me}");

            // a web server in a container, published on 8080 (Docker DNAT: the FORWARD / DOCKER-USER path)
            var web = WebPort(server);
            if (web > 0)
            {
                if (!Sh(lab, "command -v docker || true").Contains("docker")) await Run(lab, "docker-install", new());
                Pull(lab, "nginx:alpine");
                Sh(lab, "docker rm -f qk-fw-web >/dev/null 2>&1; docker run -d --name qk-fw-web -p 8080:80 nginx:alpine >/dev/null");
                for (var i = 0; i < 20 && !await Answers(web); i++) await Task.Delay(500);
                Assert.True(await Answers(web), "the container does not answer before any rule");

                var block = FwRule(FirewallAction.Block, "8080", me);
                Assert.True(fw.Apply(server, new FirewallConfig { Rules = [block] }, Log).Applied, string.Join('\n', lines));
                Assert.False(await Answers(web), "blocked for this PC, still answers");

                var allow = FwRule(FirewallAction.AllowOnly, "8080", "203.0.113.9");
                Assert.True(fw.Apply(server, new FirewallConfig { Rules = [allow] }, Log).Applied);
                Assert.False(await Answers(web), "allowed only for 203.0.113.9, still answers");
                allow.Sources.Add(me);
                Assert.True(fw.Apply(server, new FirewallConfig { Rules = [allow] }, Log).Applied);
                Assert.True(await Answers(web), "allowed for this PC, does not answer");
            }

            // a whole-server allow-list without this PC: the new login fails and the previous rules come back at once
            var keep = new FirewallConfig { Rules = [FwRule(FirewallAction.AllowOnly, "", me, "198.51.100.0/24")] };
            var applied = fw.Apply(server, keep, Log);
            Assert.True(applied.Applied, string.Join('\n', lines));
            Assert.Equal(FirewallRules.Hash(keep), applied.State?.Hash);
            var lockout = new FirewallConfig { Rules = [FwRule(FirewallAction.AllowOnly, "", "203.0.113.9")] };
            Assert.True(FirewallRules.CutsSsh(lockout, 22, System.Net.IPAddress.Parse(me)));
            var r = fw.Apply(server, lockout, Log);
            Assert.False(r.Applied);
            Assert.Equal(FirewallRules.Hash(keep), r.State?.Hash); // the rules before the failed change
            Sh(lab, "true"); // and SSH works

            // saved for the next boot: the unit restores the chains and their hooks
            Sh(lab, "iptables -D INPUT -j QK-IN && iptables -F QK-IN && systemctl restart quaykeep-firewall");
            var state = fw.Read(server)!;
            Assert.True(state.Installed);
            Assert.Equal(FirewallRules.Hash(keep), state.Hash);

            // the timer: rules loaded and never confirmed (the app lost the server) go back by themselves
            var lost = new FirewallService(lab.Vault, lab.Ssh) { RevertSeconds = 8, SkipConfirm = true };
            var later = new FirewallConfig { Rules = [FwRule(FirewallAction.Block, "8443", "203.0.113.7")] };
            lost.Apply(server, later, Log);
            Assert.Equal(FirewallRules.Hash(later), fw.Read(server)?.Hash);
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.Equal(FirewallRules.Hash(keep), fw.Read(server)?.Hash);

            // off: no chains, no unit
            fw.TurnOff(server, Log);
            Assert.False(fw.Read(server)!.Installed);
            Assert.Contains("gone", Sh(lab, "iptables -S QK-IN >/dev/null 2>&1 || echo gone; systemctl is-enabled quaykeep-firewall 2>/dev/null || true"));
            if (web > 0)
            {
                Assert.True(await Answers(web));
                Sh(lab, "docker rm -f qk-fw-web >/dev/null 2>&1", check: false);
            }
        });
    }
}
