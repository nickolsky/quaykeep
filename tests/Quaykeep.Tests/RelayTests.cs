using System.Diagnostics;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Inventory;
using Quaykeep.Core.Models;
using Xunit.Abstractions;

namespace Quaykeep.Tests;

public class RelayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("tcp", "0.0.0.0", "2001:db8::2")]
    [InlineData("udp", "::", "192.0.2.2")]
    public void Validates_And_Parses_A_Relay(string protocol, string listen, string target)
    {
        var f = RelayCommands.Validate(protocol, listen, "443", target, "");
        var id = new string('a', 32);
        var parsed = Assert.Single(RelayCommands.Parse($"{id} {protocol} {listen} 443 {target} 443\ninvalid\n"));
        Assert.Equal(f.Key, parsed.Key);
        Assert.Equal(id, parsed.RelayId);
        Assert.True(parsed.Managed);
        Assert.Equal("443", parsed.EffectiveTargetPort);
    }

    [Theory]
    [InlineData("tcp", "::", "::1", "443")]
    [InlineData("udp", "0.0.0.0", "fe80::1", "443")]
    [InlineData("tcp", "::ffff:192.0.2.1", "::1", "443")]
    [InlineData("tcp", "0.0.0.0", "::", "443")]
    [InlineData("tcp", "0.0.0.0", "::1", "1:2")]
    public void Rejects_Unsupported_Endpoints(string protocol, string listen, string target, string port) =>
        Assert.Throws<ArgumentException>(() => RelayCommands.Validate(protocol, listen, port, target, ""));

    [Fact]
    public void Only_An_Ipv4_Relay_Can_Be_Shadowed_By_Legacy_Dnat()
    {
        var nat = PortForwardService.Validate("tcp", "443", "192.0.2.2", "");
        Assert.True(PortForwardService.Shadows(nat, RelayCommands.Validate("tcp", "0.0.0.0", "443", "::1", "")));
        Assert.False(PortForwardService.Shadows(nat, RelayCommands.Validate("tcp", "::", "443", "192.0.2.2", "")));
    }

    [Fact]
    public void A_Relay_Accepts_Only_Its_Listen_Address_And_Family()
    {
        var wildcard = RelayCommands.Validate("tcp", "::", "443", "192.0.2.2", "");
        var specific = RelayCommands.Validate("tcp", "[2001:db8::1]", "443", "192.0.2.2", "");
        Assert.True(ForwardChains.AcceptsAddress(wildcard, "2001:db8::2"));
        Assert.False(ForwardChains.AcceptsAddress(wildcard, "192.0.2.1"));
        Assert.False(ForwardChains.AcceptsAddress(specific, "2001:db8::2"));
        Assert.True(ForwardChains.AcceptsAddress(specific, "2001:db8::1"));
        Assert.True(PortForwardService.Conflicts(wildcard, specific));
    }

    [Fact]
    public async Task Offline_Docker_Relays_Run_One_At_A_Time()
    {
        if (Environment.GetEnvironmentVariable("SSHM_RELAY_DOCKER") != "1") return;
        var xtables = Environment.GetEnvironmentVariable("SSHM_RELAY_XTABLES");
        foreach (var (protocol, ipv6) in new[] { ("tcp", false), ("tcp", true), ("udp", false), ("udp", true) })
        {
            var name = "qk-relay-" + Guid.NewGuid().ToString("N")[..12];
            var ingress = ipv6 ? "fd12:3456::1" : "192.0.2.1";
            var listen = protocol == "tcp" ? ingress : ipv6 ? "::" : "0.0.0.0";
            var peer = ipv6 ? "fd12:3456::2" : "192.0.2.2";
            var target = ipv6 ? "127.0.0.1" : "::1";
            var rule = RelayCommands.Validate(protocol, listen, "19443", target, "19444");
            var direction = ipv6 ? "IPv6 → IPv4" : "IPv4 → IPv6";
            output.WriteLine($"Starting {protocol} {direction} in {name}");
            try
            {
                await Docker(["run", "--detach", "--name", name, "--pull", "never", "--network", "none",
                    "--privileged", "--cgroupns", "host", "--tmpfs", "/run", "--tmpfs", "/run/lock",
                    "-v", "/sys/fs/cgroup:/sys/fs/cgroup:rw", "script-lab-rocky"]);
                if (!string.IsNullOrEmpty(xtables))
                {
                    await Docker(["cp", xtables, name + ":/tmp/qk-xtables"]);
                    await Docker(["exec", name, "chmod", "+x", "/tmp/qk-xtables/ld-linux-x86-64.so.2"]);
                }
                await Docker(["exec", name, "sh", "-ec", Setup]);
                await Docker(["exec", "--detach", name, "python3", "-c", Echo, protocol, target]);
                await Docker(["exec", name, "sh", "-ec",
                    "for n in $(seq 1 50); do test ! -e /tmp/qk-echo-ready || exit 0; sleep .2; done; exit 1"]);
                await Shell(name, RelayCommands.Change(null, [rule]));
                var installed = Assert.Single(RelayCommands.Parse(SectionParser.Split(
                    (await Shell(name, RelayCommands.Read)).Text)[RelayCommands.Section]));
                var unit = $"quaykeep-relay@{installed.RelayId}.service";
                Assert.Equal(rule.Key, installed.Key);
                await Docker(["exec", name, "systemctl", "is-enabled", "--quiet", unit]);
                await Probe(name, protocol, ingress, blocked: false);

                // Match the same INPUT hook and Quaykeep-generated policy used on real servers.
                var iptables = ipv6 ? "ip6tables" : "iptables";
                var firewallTools = await Docker(["exec", name, "sh", "-c", "command -v " + iptables + "-restore"], check: false);
                if (firewallTools.Code == 0 || !string.IsNullOrEmpty(xtables))
                {
                    var config = new FirewallConfig
                    {
                        Rules = [new FirewallRule { Action = FirewallAction.Block, Protocol = protocol, Ports = "19443", Sources = [peer] }],
                    };
                    Task<(int Code, string Text)> Firewall(string program, string[] args, string? input = null) =>
                        string.IsNullOrEmpty(xtables) ? Docker(["exec", "-i", name, program, .. args], input) :
                        Docker(["exec", "-i", "-e", "XTABLES_LIBDIR=/tmp/qk-xtables/xtables", name,
                            "/tmp/qk-xtables/ld-linux-x86-64.so.2", "--library-path", "/tmp/qk-xtables",
                            "--argv0", program, "/tmp/qk-xtables/xtables-nft-multi", .. args], input);
                    await Firewall(iptables + "-restore", ["--noflush"], FirewallRules.Payload(config, ipv6));
                    await Firewall(iptables, ["-I", "INPUT", "1", "-j", FirewallRules.InChain]);
                    await Probe(name, protocol, ingress, blocked: true);
                    await Firewall(iptables, ["-D", "INPUT", "-j", FirewallRules.InChain]);
                }
                else output.WriteLine($"Skipped the {iptables} check because the cached image lacks it.");

                if (protocol == "tcp" && !ipv6)
                {
                    var conflicting = RelayCommands.Validate(protocol, listen, "19445", "::1", "19444");
                    await Docker(["exec", "--detach", name, "python3", "-c",
                        "import socket,time; s=socket.socket(); s.bind(('0.0.0.0',19445)); s.listen(); open('/tmp/qk-conflict','w').close(); time.sleep(60)"]);
                    await Docker(["exec", name, "sh", "-ec",
                        "for n in $(seq 1 50); do test ! -e /tmp/qk-conflict || exit 0; sleep .2; done; exit 1"]);
                    var failed = await Shell(name, RelayCommands.Change(installed, [conflicting]), check: false);
                    Assert.NotEqual(0, failed.Code);
                    Assert.Contains("Could not bind", failed.Text);
                    await Docker(["exec", name, "systemctl", "is-enabled", "--quiet", unit]);
                    await Probe(name, protocol, ingress, blocked: false);
                }

                await Docker(["restart", "-t", "3", name]);
                await Docker(["exec", name, "sh", "-ec", Setup]);
                await Docker(["exec", "--detach", name, "python3", "-c", Echo, protocol, target]);
                await Docker(["exec", name, "sh", "-ec",
                    "for n in $(seq 1 60); do systemctl is-active --quiet " + unit +
                    " && test -e /tmp/qk-echo-ready && exit 0; sleep .2; done; exit 1"]);
                await Probe(name, protocol, ingress, blocked: false);
                await Shell(name, RelayCommands.Change(installed, []));
                Assert.Empty(RelayCommands.Parse(SectionParser.Split(
                    (await Shell(name, RelayCommands.Read)).Text)[RelayCommands.Section]));
                await Probe(name, protocol, ingress, blocked: true);
                output.WriteLine($"Passed {protocol} {direction}");
            }
            finally
            {
                await Docker(["rm", "-f", name], check: false);
            }
        }
    }

    private const string Setup = """
        test -z "$(ip -4 route show default)"
        test -z "$(ip -6 route show default)"
        rm -f /tmp/qk-echo-ready
        for n in $(seq 1 60); do test ! -d /run/systemd/system || break; sleep .2; done
        test -d /run/systemd/system
        ip netns add qkclient
        ip link add qk-veth type veth peer name qk-peer
        ip addr add 192.0.2.1/30 dev qk-veth
        ip -6 addr add fd12:3456::1/64 dev qk-veth nodad
        ip link set qk-veth up
        ip link set qk-peer netns qkclient
        ip -n qkclient link set lo up
        ip -n qkclient addr add 192.0.2.2/30 dev qk-peer
        ip -n qkclient -6 addr add fd12:3456::2/64 dev qk-peer nodad
        ip -n qkclient link set qk-peer up
        test -z "$(ip -n qkclient -4 route show default)"
        test -z "$(ip -n qkclient -6 route show default)"
        """;

    private const string Echo = """
        import socket, sys
        proto, addr = sys.argv[1:]
        with socket.socket(socket.AF_INET6 if ':' in addr else socket.AF_INET,
                           socket.SOCK_STREAM if proto == 'tcp' else socket.SOCK_DGRAM) as s:
            s.bind((addr, 19444))
            if proto == 'tcp':
                s.listen()
            open('/tmp/qk-echo-ready', 'w').close()
            while True:
                if proto == 'tcp':
                    client, _ = s.accept()
                    with client:
                        client.sendall(b''.join(iter(lambda: client.recv(65536), b'')))
                else:
                    data, peer = s.recvfrom(65535)
                    s.sendto(data, peer)
        """;

    private const string Client = """
        import socket, sys
        proto, addr, blocked = sys.argv[1:]
        family = socket.AF_INET6 if ':' in addr else socket.AF_INET
        kind = socket.SOCK_STREAM if proto == 'tcp' else socket.SOCK_DGRAM
        try:
            with socket.socket(family, kind) as a, socket.socket(family, kind) as b:
                a.settimeout(2)
                b.settimeout(2)
                a.connect((addr, 19443))
                a.sendall(b'first')
                if proto == 'tcp':
                    a.shutdown(socket.SHUT_WR)
                    data = b''.join(iter(lambda: a.recv(65536), b''))
                    assert data == b'first', data
                else:
                    b.connect((addr, 19443))
                    b.sendall(b'second')
                    assert b.recv(65535) == b'second'
                    assert a.recv(65535) == b'first'
                    a.send(b'')
                    assert a.recv(65535) == b''
        except (socket.timeout, TimeoutError, ConnectionRefusedError):
            if blocked != 'yes':
                raise
        else:
            assert blocked == 'no', 'unexpected reply'
        """;

    private static Task<(int Code, string Text)> Probe(string name, string protocol, string ip, bool blocked) =>
        Docker(["exec", name, "ip", "netns", "exec", "qkclient", "python3", "-c", Client, protocol, ip, blocked ? "yes" : "no"]);

    private static Task<(int Code, string Text)> Shell(string name, string script, bool check = true) =>
        Docker(["exec", "-i", name, "sh", "-s"], script, check);

    private static async Task<(int Code, string Text)> Docker(string[] args, string? input = null, bool check = true)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardInput = input != null,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg.Replace("\r\n", "\n"));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input != null)
        {
            await process.StandardInput.WriteAsync(input.EndsWith('\n') ? input : input + "\n");
            process.StandardInput.Close();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = (await stdout) + (await stderr);
        if (check) Assert.True(process.ExitCode == 0, $"docker {string.Join(' ', args.Take(4))}: {text}");
        return (process.ExitCode, text);
    }
}
