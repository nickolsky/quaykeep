using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;

namespace Quaykeep.Tests;

public class AwsSsmTests
{
    private static ServerEntry Instance(string? profile = null, string? region = null) =>
        new() { Host = "i-0cac78034f134d1fc", Port = 22, Username = "ec2-user", Ssm = true, SsmProfile = profile, SsmRegion = region };

    [Fact]
    public void Start_Session_Tunnels_To_Sshd_With_The_Profile_And_Region_Only_When_Set()
    {
        Assert.Equal(["ssm", "start-session", "--target", "i-0cac78034f134d1fc", "--document-name", "AWS-StartSSHSession",
            "--parameters", "portNumber=22"], AwsSsm.StartSessionArgs(Instance(), "i-0cac78034f134d1fc", "22"));
        Assert.Equal(["ssm", "start-session", "--target", "i-1", "--document-name", "AWS-StartSSHSession",
            "--parameters", "portNumber=2222", "--profile", "396517496207_AWSAdministratorAccess", "--region", "eu-west-1"],
            AwsSsm.StartSessionArgs(Instance(" 396517496207_AWSAdministratorAccess ", "eu-west-1"), "i-1", "2222"));
    }

    [Fact]
    public void Proxy_Command_Quotes_A_Path_With_Spaces()
    {
        var s = Instance("396517496207_AWSAdministratorAccess");
        Assert.Equal("aws ssm start-session --target i-0cac78034f134d1fc --document-name AWS-StartSSHSession --parameters portNumber=22 " +
                     "--profile 396517496207_AWSAdministratorAccess", AwsSsm.ProxyCommand(s, "aws"));
        Assert.StartsWith("\"C:\\Program Files\\Amazon\\AWSCLIV2\\aws.exe\" ssm ",
            AwsSsm.ProxyCommand(s, @"C:\Program Files\Amazon\AWSCLIV2\aws.exe"));
    }

    [Fact]
    public void Shell_Only_Opens_The_Plain_Session_And_Turns_Ssh_Features_Off()
    {
        var s = Instance("396517496207_AWSAdministratorAccess");
        s.Auth = AuthMode.None;
        Assert.False(s.SshLogin);
        Assert.False(Quaykeep.Core.Inventory.ServerInventoryService.Supported(s));
        Assert.Equal(["ssm", "start-session", "--target", "i-0cac78034f134d1fc", "--profile", "396517496207_AWSAdministratorAccess"],
            AwsSsm.ShellArgs(s));
        Assert.Equal("aws ssm start-session --target i-0cac78034f134d1fc --profile 396517496207_AWSAdministratorAccess",
            AwsSsm.ShellCommandLine(s, "aws"));
        s.Auth = AuthMode.Key;
        Assert.True(Quaykeep.Core.Inventory.ServerInventoryService.Supported(s));
    }

    [Fact]
    public void Commands_Go_As_Json_And_The_Key_Script_Quotes_Its_Values()
    {
        var args = AwsSsm.CommandArgs(Instance(region: "eu-west-1"), "sh -c 'echo a,b'");
        Assert.Equal(["ssm", "start-session", "--target", "i-0cac78034f134d1fc", "--document-name", "AWS-StartNonInteractiveCommand",
            "--parameters"], args[..7]);
        Assert.Equal("sh -c 'echo a,b'", (string?)System.Text.Json.Nodes.JsonNode.Parse(args[7])!["command"]![0]);
        Assert.Equal(["--region", "eu-west-1"], args[8..]);

        var script = AwsSsm.InstallKeyScript("ubuntu", "ssh-ed25519 AAAA test@host");
        Assert.StartsWith("u='ubuntu'; pub='ssh-ed25519 AAAA test@host'\n", script);
        Assert.Contains("sudo -n", script);
        Assert.EndsWith("echo SSHM_OK", script.TrimEnd());
        Assert.DoesNotContain('\r', script);
    }

    [Fact]
    public void Cli_Errors_Are_Explained()
    {
        var s = Instance("prod");
        Assert.Null(AwsSsm.Explain("  ", s));
        Assert.Equal(L.F("Ssm.SsoExpired", "prod"),
            AwsSsm.Explain("Error when retrieving token from sso: Token has expired and refresh failed", s));
        Assert.Equal(L.F("Ssm.SsoExpired", "default"), AwsSsm.Explain(
            "The SSO session associated with this profile has expired or is otherwise invalid. To refresh this SSO session run aws sso login",
            Instance()));
        Assert.Equal(L.Get("Ssm.NoPlugin"), AwsSsm.Explain("SessionManagerPlugin is not found. Please refer to SessionManager Documentation", s));
        Assert.Equal(L.F("Ssm.Failed", "An error occurred (TargetNotConnected)"),
            AwsSsm.Explain("An error occurred (TargetNotConnected)\n", s));
    }

    /// <summary>A stand-in for the CLI that copies stdin to stdout.</summary>
    private static ProcessStartInfo Echo()
    {
        if (!OperatingSystem.IsWindows()) return new ProcessStartInfo("cat");
        var psi = new ProcessStartInfo(SessionLauncher.FindOnPath("pwsh.exe") ?? "powershell.exe");
        foreach (var a in new[] { "-NoProfile", "-Command", "[Console]::OpenStandardInput().CopyTo([Console]::OpenStandardOutput())" })
            psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public async Task Tunnel_Pipes_One_Connection_And_Ends_With_It()
    {
        using var tunnel = new SsmTunnel(Echo());
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, tunnel.Port);
            var stream = client.GetStream();
            var sent = Encoding.ASCII.GetBytes("SSH-2.0-test\r\n");
            await stream.WriteAsync(sent);
            var got = new byte[sent.Length];
            await stream.ReadExactlyAsync(got).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(sent, got);

            using var second = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => second.ConnectAsync(IPAddress.Loopback, tunnel.Port));
        }
        var watch = Stopwatch.StartNew();
        tunnel.WaitErrors(TimeSpan.FromSeconds(10));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "the process outlived the connection");
    }

    [Fact]
    public async Task Tunnel_Keeps_What_The_Cli_Said_Before_It_Quit()
    {
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("sh");
        foreach (var a in OperatingSystem.IsWindows()
                     ? new[] { "/c", "echo Token has expired and refresh failed 1>&2" }
                     : ["-c", "echo Token has expired and refresh failed >&2"])
            psi.ArgumentList.Add(a);
        using var tunnel = new SsmTunnel(psi);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, tunnel.Port);
        var read = await client.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, read);
        Assert.Contains("Token has expired", tunnel.WaitErrors(TimeSpan.FromSeconds(10)));
    }
}
