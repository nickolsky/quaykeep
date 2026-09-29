using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Mcp;

/// <summary>
/// The server's firewall: listing at every level, changes at full access only. Every change asks the user, keeps the
/// rules only when a new login works (like the app) and never takes a rule that would cut Quaykeep's own SSH access.
/// </summary>
public sealed partial class McpServer
{
    private FirewallService? _firewall;
    private FirewallService FirewallSvc => _firewall ??= new FirewallService(_host.Vault, _host.Ssh);

    private IEnumerable<McpTool> FirewallTools() =>
    [
        new("list_firewall", "List firewall rules",
            "Quaykeep's firewall rules of the server (block these IPs / allow only these IPs, per port or the whole server), " +
            "whether the server has them in force, and this PC's address as the server sees it.",
            McpAccess.ReadOnly, Schema(ServerProp, new Prop("refresh", "boolean", "Read the state from the server now.")), ListFirewall),
        new("add_firewall_rule", "Add firewall rule",
            "Adds a rule and applies the server's rules (the user confirms it in Quaykeep). Order on the server: blocks, then " +
            "port allow-lists, then the whole-server allow-list; loopback, open connections and DHCP always pass. Rules only " +
            "restrict: they never open what the server's own firewall closes. A rule that would cut Quaykeep's SSH access is refused.",
            McpAccess.Full, Schema(ServerProp,
                new Prop("action", "string", "block: these sources may not connect; allow_only: only these sources may.", true, ["block", "allow_only"]),
                new Prop("sources", "string", "IPv4 / IPv6 addresses and networks, comma-separated, e.g. \"203.0.113.5, 10.0.0.0/8\".", true),
                new Prop("ports", "string", "\"443\", \"80,443\", \"8000-8100\"; leave out for the whole server."),
                new Prop("protocol", "string", "For ports (default tcp).", Enum: ["tcp", "udp", "tcp,udp"]),
                new Prop("comment", "string", "Shown next to the rule in Quaykeep.")), AddFirewallRule,
            ReadOnly: false, Destructive: true),
        new("remove_firewall_rule", "Remove firewall rule",
            "Removes a rule (by the id from list_firewall) and applies the server's remaining rules (the user confirms it in Quaykeep).",
            McpAccess.Full, Schema(ServerProp, new Prop("rule_id", "string", "The rule's id from list_firewall.", true)), RemoveFirewallRule,
            ReadOnly: false, Destructive: true),
    ];

    private static object RuleData(FirewallRule r) => new
    {
        id = r.Id,
        action = r.Action == FirewallAction.Block ? "block" : "allow_only",
        target = r.WholeServer ? "whole_server" : "ports",
        protocol = r.WholeServer ? null : r.Protocol,
        ports = r.WholeServer ? null : FirewallRules.NormalizePorts(r.Ports).Replace(':', '-'),
        sources = r.Sources,
        comment = r.Comment,
        enabled = r.Enabled,
    };

    private async Task<ToolResult> ListFirewall(ToolCall c)
    {
        var s = c.S;
        string? me = null;
        if (c.Bool("refresh") || s.Facts?.Firewall == null)
        {
            CheckDirect(s);
            me = await Task.Run(() =>
            {
                FirewallSvc.Read(s);
                return FirewallSvc.ClientAddress(s)?.ToString();
            }, c.Ct);
            s = Fresh(s);
        }
        var rules = s.Firewall?.Rules ?? [];
        var status = FirewallRules.Status(s.Firewall, s.Facts?.Firewall);
        return ToolResult.Data(new
        {
            status = status.ToString().ToLowerInvariant(),
            status_text = FirewallRules.StatusText(status),
            this_pc = me,
            rules = rules.Select(RuleData).ToList(),
        }, $"{rules.Count} rules, {status}");
    }

    private async Task<ToolResult> AddFirewallRule(ToolCall c)
    {
        var s = c.S;
        CheckDirect(s);
        var rule = new FirewallRule
        {
            Action = c.Required("action") == "allow_only" ? FirewallAction.AllowOnly : FirewallAction.Block,
            Sources = FirewallRules.ParseSources(c.Required("sources")),
            Ports = FirewallRules.NormalizePorts(c.Str("ports")),
            Protocol = c.Str("protocol", "tcp"),
            Comment = c.Str("comment") is { Length: > 0 } comment ? comment : null,
        };
        try
        {
            FirewallRules.Validate(rule);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        rule.Sources = rule.Sources.Select(FirewallRules.Canonical).Distinct().ToList();
        var config = s.Firewall?.Clone() ?? new FirewallConfig();
        config.Rules.Add(rule);
        return await ChangeFirewall(c, config, L.F("Mcp.ConfirmFirewallAdd", s.Name, FirewallRules.Describe(rule)), RuleData(rule));
    }

    private async Task<ToolResult> RemoveFirewallRule(ToolCall c)
    {
        var s = c.S;
        CheckDirect(s);
        var id = c.Required("rule_id");
        var config = s.Firewall?.Clone() ?? new FirewallConfig();
        if (config.Rules.FirstOrDefault(r => r.Id == id) is not { } rule) return ToolResult.Fail(L.F("Mcp.FirewallNoRule", s.Name, id));
        config.Rules.Remove(rule);
        return await ChangeFirewall(c, config, L.F("Mcp.ConfirmFirewallRemove", s.Name, FirewallRules.Describe(rule)), RuleData(rule));
    }

    /// <summary>Asks the user, keeps the new rules in the vault and applies them; the old ones come back when the login check fails.</summary>
    private async Task<ToolResult> ChangeFirewall(ToolCall c, FirewallConfig config, string confirmText, object changed)
    {
        var s = c.S;
        var me = await Task.Run(() => FirewallSvc.ClientAddress(s), c.Ct);
        if (me != null && FirewallRules.CutsSsh(config, s.Port, me)) return ToolResult.Fail(L.F("Mcp.FirewallCutsSsh", me));
        if (!await Confirm(c, confirmText)) throw new McpDeniedException(L.Get("Mcp.UserDeclined"));

        var previous = s.Firewall?.Clone();
        void Store(FirewallConfig? value) => _host.Vault.Update(d =>
        {
            if (d.Servers.FirstOrDefault(x => x.Id == s.Id) is { } entry) entry.Firewall = value;
        });
        Store(config);
        var log = new List<string>();
        FirewallResult result;
        try
        {
            result = await Task.Run(() => FirewallSvc.Apply(s, config, l => { lock (log) log.Add(l); }), c.Ct);
        }
        catch
        {
            Store(previous);
            throw;
        }
        _host.Changed?.Invoke(s.Id);
        if (!result.Applied)
        {
            Store(previous);
            return ToolResult.Fail(L.Get("Mcp.FirewallReverted") + "\n" + string.Join("\n", log));
        }
        return ToolResult.Data(new { applied = true, persisted = result.Persisted, rule = changed, log }, "applied");
    }
}
