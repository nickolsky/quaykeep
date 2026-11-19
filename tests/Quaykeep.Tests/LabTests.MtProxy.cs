using Quaykeep.Core.Mcp;
using Quaykeep.Core.Scripts;

namespace Quaykeep.Tests;

public partial class LabTests
{
    [Fact]
    public async Task MtProxy_Listens_And_Reuses_The_Secret()
    {
        if (!Enabled("mtproxy")) return;
        await OnEachServer(async lab =>
        {
            const string unit = "quaykeep-mtproxy.service";
            const string port = "18443";
            var host = $"{Guid.NewGuid():N}.lab.test";
            var values = new Dictionary<string, string>
            {
                ["MTPROXY_PORT"] = port, ["SERVER_ADDRESS"] = host, ["MTPROXY_PUBLIC_IP"] = "192.0.2.100",
            };
            var installed = await Run(lab, "mtproxy", values);
            var link = installed["MTPROXY_URL"];
            Assert.StartsWith("tg://proxy?", link);
            Assert.True(QrPayloads.CanShow(link));
            Assert.True(ScriptSecrets.IsSecret("MTPROXY_URL", link));
            var query = Query(link);
            Assert.Equal(host, query["server"]);
            Assert.Equal(port, query["port"]);
            Assert.Matches("^dd[0-9a-f]{32}$", query["secret"]);
            Assert.Equal(port, installed["MTPROXY_PORT"]);
            Sh(lab, $"systemctl is-active --quiet {unit} && systemctl is-enabled --quiet {unit}");
            Sh(lab, $"timeout 3 bash -c 'exec 3<>/dev/tcp/127.0.0.1/{port}'");
            Sh(lab, "test \"$(stat -Lc %a /opt/quaykeep-mtproxy/current/service.env)\" = 600");
            var args = Sh(lab, $"ps -p $(systemctl show -p MainPID --value {unit}) -o args=");
            Assert.DoesNotContain(query["secret"][2..], args);
            // Upstream parses the NAT pair by replacing the colon in argv with a NUL.
            Assert.Matches(@"--nat-info \d+\.\d+\.\d+\.\d+[: ]192\.0\.2\.100\s", args);

            var rerun = await Run(lab, "mtproxy", values, "-rerun", optionalResult: "MTPROXY_URL");
            Assert.False(rerun.ContainsKey("MTPROXY_URL"));
            Assert.Equal(port, rerun["MTPROXY_PORT"]);

            const string newPort = "18444";
            values["MTPROXY_PORT"] = newPort;
            var moved = await Run(lab, "mtproxy", values, "-new-port");
            var newQuery = Query(moved["MTPROXY_URL"]);
            Assert.Equal(newPort, newQuery["port"]);
            Assert.Equal(query["secret"], newQuery["secret"]);
            Sh(lab, $"timeout 3 bash -c 'exec 3<>/dev/tcp/127.0.0.1/{newPort}'");
            Assert.Empty(Sh(lab, $"ss -Hln 'sport = :{port}'").Trim());
        });
    }
}
