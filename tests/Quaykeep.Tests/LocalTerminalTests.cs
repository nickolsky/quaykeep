using System.Diagnostics;
using System.Text;
using Quaykeep.Core.Models;
using Quaykeep.Core.Terminal;

namespace Quaykeep.Tests;

public class LocalTerminalTests
{
    [Fact]
    public void Command_Line_Is_Quoted_Like_The_C_Runtime_Splits_It()
    {
        Assert.Equal(@"aws ssm start-session --target i-1", LocalTerminalSession.CommandLine("aws", ["ssm", "start-session", "--target", "i-1"]));
        // a backslash before a quote, or before the closing quote, is doubled; elsewhere it stays single
        Assert.Equal(@"""C:\Program Files\aws.exe"" ""a b"" """" ""say \""hi\"""" ""C:\my dir\\"" plain\path",
            LocalTerminalSession.CommandLine(@"C:\Program Files\aws.exe", ["a b", "", "say \"hi\"", @"C:\my dir\", @"plain\path"]));
    }

    private static async Task<(string Output, string? Error)> RunAsync(string command)
    {
        var psi = new ProcessStartInfo("cmd.exe");
        foreach (var a in new[] { "/c", command }) psi.ArgumentList.Add(a);
        using var session = new LocalTerminalSession(new ServerEntry { Name = "local" }, psi);
        var output = new StringBuilder();
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += t =>
        {
            lock (output) output.Append(t);
        };
        session.Closed += e => closed.TrySetResult(e);
        session.Open(80, 24);
        var error = await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lock (output) return (output.ToString(), error);
    }

    [Fact]
    public async Task A_Program_Runs_In_The_Console_And_Its_End_Is_Reported()
    {
        if (!OperatingSystem.IsWindows() || !LocalTerminalSession.Available) return;
        var (output, error) = await RunAsync("echo hello from conpty");
        Assert.Contains("hello from conpty", output);
        Assert.Null(error);

        (_, error) = await RunAsync("exit 3");
        Assert.Equal(L.F("Term.ExitCode", 3), error);
    }

    [Fact]
    public void Commands_Are_Refused_Before_Anything_Starts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var session = new LocalTerminalSession(new ServerEntry(), new ProcessStartInfo("cmd.exe"));
        Assert.Throws<InvalidOperationException>(() => session.Open(80, 24, "ls"));
        Assert.Throws<InvalidOperationException>(() => session.Run("ls", TimeSpan.FromSeconds(1)));
    }
}
