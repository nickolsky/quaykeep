using System.Net;
using Renci.SshNet;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;
using Quaykeep.Core.Storage;

namespace Quaykeep.Core.Firewall;

/// <summary>What happened to a rule set on one server.</summary>
/// <param name="Applied">The rules are in force and a new login works.</param>
/// <param name="Persisted">Saved to come back after a reboot.</param>
public sealed record FirewallResult(bool Applied, bool Persisted, FirewallState? State);

/// <summary>
/// Applies, reads and removes Quaykeep's firewall on a server (root or sudo). A change is kept only when a new SSH login
/// still works: the rules go in with a timer on the server that puts the old ones back, and only a fresh connection
/// cancels it. The connection that applies them stays open (an established connection always passes), so a failed
/// login is undone at once; if that connection drops too, the timer does it.
/// </summary>
public sealed class FirewallService(VaultService vault, SshClientFactory ssh)
{
    /// <summary>Seconds until the server puts the previous rules back by itself; longer than a timed-out login attempt.</summary>
    public const int DefaultRevertSeconds = 90;

    internal int RevertSeconds { get; init; } = DefaultRevertSeconds;
    /// <summary>Tests of the timer: apply and leave, as if the app had lost the server.</summary>
    internal bool SkipConfirm { get; init; }

    public FirewallResult Apply(ServerEntry server, FirewallConfig config, Action<string> log)
    {
        FirewallRules.Validate(config);
        log(L.F("Fw.Connecting", server.Display));
        using var admin = Open(server);
        var r = Run(admin, server, FirewallRules.Apply(config, RevertSeconds));
        if (!r.Output.Contains(FirewallRules.AppliedMarker)) throw new InvalidOperationException(L.Get("Fw.Failed") + "\n" + r.Combined);
        log(L.F("Fw.Loaded", RevertSeconds));
        if (SkipConfirm) return new FirewallResult(false, false, null);

        SshClient check;
        try
        {
            check = ConnectFresh(server);
            if (!check.RunCommand("echo QK_OK").Result.Contains("QK_OK")) throw new InvalidOperationException(L.Get("Fw.CheckFailed"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log(L.F("Fw.LoginFailed", ex.Message));
            try
            {
                var back = Run(admin, server, FirewallRules.RevertNow);
                log(back.Output.Contains(FirewallRules.RevertedMarker) ? L.Get("Fw.Reverted") : L.F("Fw.RevertUnclear", back.Combined));
            }
            catch (Exception ex2)
            {
                log(L.F("Fw.RevertByTimer", RevertSeconds, ex2.Message));
            }
            return new FirewallResult(false, false, TryRead(admin, server));
        }

        using (check)
        {
            var c = Run(check, server, FirewallRules.Confirm);
            if (!c.Output.Contains(FirewallRules.ConfirmedMarker)) throw new InvalidOperationException(L.Get("Fw.ConfirmFailed") + "\n" + c.Combined);
            var persisted = FirewallRules.PersistResult(c.Output) is "systemd";
            log(L.Get("Fw.LoginWorks") + " " + L.Get(persisted ? "Fw.Persisted" : "Fw.NotPersisted"));
            return new FirewallResult(true, persisted, TryRead(check, server));
        }
    }

    /// <summary>Removes the Quaykeep chains, their hooks and the boot unit. Taking restrictions away cannot lock anyone out.</summary>
    public FirewallState? TurnOff(ServerEntry server, Action<string> log)
    {
        log(L.F("Fw.Connecting", server.Display));
        using var client = Open(server);
        var r = Run(client, server, FirewallRules.TurnOff);
        if (!r.Output.Contains(FirewallRules.OffMarker)) throw new InvalidOperationException(L.Get("Fw.Failed") + "\n" + r.Combined);
        log(L.Get("Fw.Off"));
        return TryRead(client, server);
    }

    /// <summary>The rules in force on the server now (also stored in the server's facts).</summary>
    public FirewallState? Read(ServerEntry server)
    {
        using var client = Open(server);
        return TryRead(client, server);
    }

    /// <summary>This PC's address as the server sees it (after NAT): what an allow-list must contain to keep SSH.</summary>
    public IPAddress? ClientAddress(ServerEntry server)
    {
        using var client = Open(server);
        return ClientAddress(client);
    }

    internal static IPAddress? ClientAddress(SshClient client)
    {
        var text = client.RunCommand("echo \"${SSH_CLIENT%% *}\"").Result.Trim();
        return IPAddress.TryParse(text, out var ip) ? ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip : null;
    }

    private FirewallState? TryRead(SshClient client, ServerEntry server)
    {
        var r = RemoteShell.Run(client, server, FirewallRules.ReadScript, elevated: true);
        var state = FirewallRules.ParseState(r.Output);
        if (state != null) vault.UpdateFacts(server.Id, f => f.Firewall = state);
        return state;
    }

    private static ShellResult Run(SshClient client, ServerEntry server, string script)
    {
        var r = RemoteShell.Run(client, server, script, elevated: true, TimeSpan.FromMinutes(5));
        if (RemoteShell.SudoFailed(r)) throw new InvalidOperationException(L.Get("Fw.NeedRoot") + "\n" + r.Error.Trim());
        return r;
    }

    private SshClient Open(ServerEntry server)
    {
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) throw new InvalidOperationException(L.Get("Inventory.JumpHost"));
        return ssh.Connect(server);
    }

    /// <summary>A new connection; a dropped packet makes it time out, so it is tried twice at most (well within the timer).</summary>
    private SshClient ConnectFresh(ServerEntry server)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ssh.Connect(server);
            }
            catch (Exception ex) when (attempt < 2 && ex is not Renci.SshNet.Common.SshAuthenticationException and not HostKeyNotTrustedException)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }
}
