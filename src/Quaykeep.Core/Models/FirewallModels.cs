namespace Quaykeep.Core.Models;

public enum FirewallAction
{
    /// <summary>The listed sources may not reach the port (or the server).</summary>
    Block,
    /// <summary>Only the listed sources may reach the port (or the server); everyone else is dropped.</summary>
    AllowOnly,
}

public enum FirewallStatus
{
    /// <summary>No rules and nothing on the server.</summary>
    None,
    /// <summary>Rules in the vault, none on the server.</summary>
    NotApplied,
    /// <summary>The server has exactly these rules.</summary>
    Applied,
    /// <summary>The server has Quaykeep rules, but not these (changed since, or applied from elsewhere).</summary>
    Changed,
    /// <summary>Not read from the server yet.</summary>
    Unknown,
}

/// <summary>One rule of a server's firewall as the user set it (the desired state, kept in the vault).</summary>
public sealed class FirewallRule
{
    public string Id { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    public FirewallAction Action { get; set; }
    /// <summary>"tcp", "udp" or "tcp,udp"; not used by whole-server rules.</summary>
    public string Protocol { get; set; } = "tcp";
    /// <summary>"443", "8000:8100", "80,443"; empty = the whole server.</summary>
    public string Ports { get; set; } = "";
    /// <summary>IPv4 / IPv6 addresses and networks (CIDR).</summary>
    public List<string> Sources { get; set; } = [];
    public string? Comment { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Sources that are the address of a server in Quaykeep: followed when that server's address changes.</summary>
    public List<FirewallLink> Links { get; set; } = [];

    public bool WholeServer => string.IsNullOrWhiteSpace(Ports);

    public FirewallRule Clone()
    {
        var c = (FirewallRule)MemberwiseClone();
        c.Sources = [.. Sources];
        c.Links = Links.Select(l => l.Clone()).ToList();
        return c;
    }
}

/// <summary>A source address that belongs to a server in Quaykeep.</summary>
public sealed class FirewallLink
{
    public Guid ServerId { get; set; }
    public string Address { get; set; } = "";

    public FirewallLink Clone() => (FirewallLink)MemberwiseClone();
}

public sealed class FirewallConfig
{
    public List<FirewallRule> Rules { get; set; } = [];
    /// <summary>Presets (<see cref="VaultData.FirewallPresets"/>) the server uses after its own rules.</summary>
    public List<Guid> Presets { get; set; } = [];

    public FirewallConfig Clone() => new() { Rules = Rules.Select(r => r.Clone()).ToList(), Presets = [.. Presets] };
}

/// <summary>A named rule set kept once and used by several servers; changing it changes all of them.</summary>
public sealed class FirewallPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<FirewallRule> Rules { get; set; } = [];

    public FirewallPreset Clone() => new() { Id = Id, Name = Name, Rules = Rules.Select(r => r.Clone()).ToList() };
}

/// <summary>What the server's Quaykeep chains hold now (read with iptables -S; part of the facts).</summary>
public sealed class FirewallState
{
    /// <summary>The chain is hooked into INPUT, i.e. the rules are in force.</summary>
    public bool Installed { get; set; }
    /// <summary>Hash of the rule set that was applied (from the chain's marker rule).</summary>
    public string? Hash { get; set; }
    public List<string> RuleIds { get; set; } = [];
    public DateTime Read { get; set; } = DateTime.Now;
}
