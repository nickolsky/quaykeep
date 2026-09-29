using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Quaykeep.Core.Forwarding;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Firewall;

/// <summary>
/// Quaykeep's firewall on a server: its own chains, QK-IN (first rule of INPUT) and QK-FWD (first rule of FORWARD and
/// DOCKER-USER: connections DNAT-ed to Docker containers and to port forwards), in iptables and ip6tables. The rules only
/// restrict: what they let through goes on to the server's own rules (ufw, firewalld…), which may still refuse it.
/// Order: loopback, established connections, DHCP (and ICMPv6) always pass; then block rules; then port allow-lists; then
/// the whole-server allow-list. Every value in a rule is validated before it reaches a script; the scripts are POSIX sh
/// and run as root.
/// </summary>
public static partial class FirewallRules
{
    public const string InChain = "QK-IN";
    public const string FwdChain = "QK-FWD";
    public const string CommentPrefix = "qk:";
    public const string HashPrefix = "qk-fw:";
    public const string AppliedMarker = "QK_FW_APPLIED";
    public const string ConfirmedMarker = "QK_FW_CONFIRMED";
    public const string RevertedMarker = "QK_FW_REVERTED";
    public const string OffMarker = "QK_FW_OFF";
    public const string PersistMarker = "QK_FW_PERSIST=";
    internal const string Section = "fw";
    private const int MaxPortItems = 50;
    private const int MaxSources = 500;

    [GeneratedRegex(@"^\d{1,5}(:\d{1,5})?$")]
    private static partial Regex PortItemRegex();

    // ---------- input ----------

    /// <summary>Addresses from free text: commas, semicolons, spaces or line breaks between them.</summary>
    public static List<string> ParseSources(string text) =>
        text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"443, 8000-8100" → "443,8000:8100".</summary>
    public static string NormalizePorts(string text) =>
        string.Join(',', text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Replace('-', ':')));

    private static List<string> PortItems(string ports) => NormalizePorts(ports).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool ValidPortItem(string p)
    {
        if (!PortItemRegex().IsMatch(p)) return false;
        var n = p.Split(':').Select(x => int.TryParse(x, out var v) ? v : 0).ToArray();
        return n.All(v => v is >= 1 and <= 65535) && (n.Length == 1 || n[0] <= n[1]);
    }

    /// <summary>An address or a network (CIDR); host bits of a network are cleared ("10.1.2.3/8" → 10.0.0.0/8).</summary>
    public static bool TryNetwork(string text, out IPNetwork network)
    {
        network = default;
        var slash = text.IndexOf('/');
        var addr = slash < 0 ? text : text[..slash];
        if (addr.Contains('%') || !IPAddress.TryParse(addr, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], System.Globalization.NumberStyles.None, null, out prefix) || prefix > max)) return false;
        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        network = new IPNetwork(new IPAddress(bytes), prefix);
        return true;
    }

    /// <summary>How a source goes into a rule: "1.2.3.4" for one address, "10.0.0.0/8" for a network.</summary>
    public static string Canonical(string source)
    {
        if (!TryNetwork(source, out var n)) throw new ArgumentException(L.F("Fw.BadSource", source));
        var full = n.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return n.PrefixLength == full ? n.BaseAddress.ToString() : n.ToString();
    }

    /// <summary>Throws ArgumentException (with a message for the user) when a rule cannot be applied.</summary>
    public static void Validate(FirewallRule r)
    {
        if (!r.WholeServer)
        {
            if (r.Protocol is not ("tcp" or "udp" or "tcp,udp")) throw new ArgumentException(L.Get("Fw.BadProtocol"));
            var items = PortItems(r.Ports);
            if (items.Count is 0 or > MaxPortItems) throw new ArgumentException(L.F("Fw.BadPort", r.Ports));
            foreach (var p in items)
                if (!ValidPortItem(p)) throw new ArgumentException(L.F("Fw.BadPort", p));
        }
        if (r.Sources.Count == 0) throw new ArgumentException(L.Get("Fw.NoSources"));
        if (r.Sources.Count > MaxSources) throw new ArgumentException(L.F("Fw.TooManySources", MaxSources));
        foreach (var s in r.Sources)
            if (!TryNetwork(s, out _)) throw new ArgumentException(L.F("Fw.BadSource", s));
    }

    public static void Validate(FirewallConfig config)
    {
        foreach (var r in config.Rules) Validate(r);
    }

    // ---------- rule set ----------

    /// <summary>Changes whenever an enabled rule changes; stored on the server to tell whether it has these rules.</summary>
    public static string Hash(FirewallConfig config)
    {
        var sb = new StringBuilder();
        foreach (var r in config.Rules.Where(r => r.Enabled))
            sb.Append(r.Id).Append('|').Append(r.Action).Append('|').Append(r.WholeServer ? "" : r.Protocol).Append('|')
                .Append(NormalizePorts(r.Ports)).Append('|')
                .AppendJoin(',', r.Sources.Select(s => TryNetwork(s, out var n) ? n.ToString() : s).Order(StringComparer.Ordinal)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12].ToLowerInvariant();
    }

    /// <summary>iptables-restore --noflush input that replaces the two Quaykeep chains (and nothing else).</summary>
    public static string Payload(FirewallConfig config, bool v6)
    {
        var rules = config.Rules.Where(r => r.Enabled).ToList();
        var sb = new StringBuilder("*filter\n:" + InChain + " - [0:0]\n:" + FwdChain + " - [0:0]\n");
        void In(string rule) => sb.Append("-A ").Append(InChain).Append(' ').Append(rule).Append('\n');
        void Fwd(string rule) => sb.Append("-A ").Append(FwdChain).Append(' ').Append(rule).Append('\n');
        void Both(string match, string portMatchIn, string portMatchFwd, string rest)
        {
            In(string.Join(' ', new[] { match, portMatchIn, rest }.Where(x => x.Length > 0)));
            Fwd(string.Join(' ', new[] { match, portMatchFwd, rest }.Where(x => x.Length > 0)));
        }
        List<string> Sources(FirewallRule r) => r.Sources.Where(s => TryNetwork(s, out var n) &&
            n.BaseAddress.AddressFamily == (v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)).Select(Canonical).Distinct().ToList();
        static string Tag(FirewallRule r) => $"-m comment --comment \"{CommentPrefix}{r.Id}\"";
        // each (protocol, port) of a port rule: INPUT matches the port, FORWARD the port before DNAT
        static IEnumerable<(string Proto, string In, string Fwd)> Targets(FirewallRule r) =>
            from proto in r.Protocol.Split(',')
            from port in PortItems(r.Ports)
            select ($"-p {proto}", $"--dport {port}", $"-m conntrack --ctorigdstport {port}");

        // always let through: loopback (the rule also carries the hash), replies, DHCP, ICMPv6 (neighbour discovery)
        In($"-i lo -m comment --comment \"{HashPrefix}{Hash(config)}\" -j RETURN");
        In("-m conntrack --ctstate ESTABLISHED,RELATED -j RETURN");
        if (v6)
        {
            In("-p ipv6-icmp -j RETURN");
            In("-p udp --sport 547 --dport 546 -j RETURN");
        }
        else In("-p udp --sport 67 --dport 68 -j RETURN");
        Fwd("-m conntrack --ctstate ESTABLISHED,RELATED -j RETURN");
        Fwd("-m conntrack ! --ctstate DNAT -j RETURN"); // containers' own traffic is not filtered, only connections to them

        foreach (var r in rules.Where(r => r.Action == FirewallAction.Block))
            foreach (var src in Sources(r))
                if (r.WholeServer) Both($"-s {src}", "", "", $"{Tag(r)} -j DROP");
                else
                    foreach (var t in Targets(r))
                        Both($"{t.Proto} -s {src}", t.In, t.Fwd, $"{Tag(r)} -j DROP");

        foreach (var r in rules.Where(r => r.Action == FirewallAction.AllowOnly && !r.WholeServer))
            foreach (var t in Targets(r))
            {
                foreach (var src in Sources(r))
                    Both($"{t.Proto} -s {src}", t.In, t.Fwd, $"{Tag(r)} -j RETURN");
                Both(t.Proto, t.In, t.Fwd, $"{Tag(r)} -j DROP");
            }

        var whole = rules.Where(r => r.Action == FirewallAction.AllowOnly && r.WholeServer).ToList();
        if (whole.Count > 0)
        {
            foreach (var r in whole)
                foreach (var src in Sources(r))
                    Both($"-s {src}", "", "", $"{Tag(r)} -j RETURN");
            Both("", "", "", $"{Tag(whole[0])} -j DROP");
        }
        return sb.Append("COMMIT\n").ToString();
    }

    // ---------- for people ----------

    /// <summary>"tcp 22, 8000-8100" or "Whole server".</summary>
    public static string TargetText(FirewallRule r) =>
        r.WholeServer ? L.Get("Fw.WholeServer") : $"{ProtocolText(r.Protocol)} {NormalizePorts(r.Ports).Replace(':', '-').Replace(",", ", ")}";

    public static string ProtocolText(string protocol) => protocol == "tcp,udp" ? "tcp+udp" : protocol;

    /// <summary>The first few sources, "+N" for the rest.</summary>
    public static string SourcesText(FirewallRule r, int max = 3) =>
        string.Join(", ", r.Sources.Take(max)) + (r.Sources.Count > max ? $" +{r.Sources.Count - max}" : "");

    /// <summary>"Block these IPs: 203.0.113.5 → tcp 22".</summary>
    public static string Describe(FirewallRule r) =>
        $"{L.Get(r.Action == FirewallAction.Block ? "Fw.Block" : "Fw.AllowOnly")}: {SourcesText(r)} → {TargetText(r)}";

    /// <summary>How the rules in the vault relate to what the server has.</summary>
    public static FirewallStatus Status(FirewallConfig? config, FirewallState? state)
    {
        var hasRules = config?.Rules.Any(r => r.Enabled) == true;
        if (state == null) return hasRules ? FirewallStatus.Unknown : FirewallStatus.None;
        if (!state.Installed) return hasRules ? FirewallStatus.NotApplied : FirewallStatus.None;
        return state.Hash == Hash(config ?? new FirewallConfig()) ? FirewallStatus.Applied : FirewallStatus.Changed;
    }

    public static string StatusText(FirewallStatus s) => L.Get(s switch
    {
        FirewallStatus.Applied => "Fw.StateApplied",
        FirewallStatus.Changed => "Fw.StateChanged",
        FirewallStatus.NotApplied => "Fw.StateNotApplied",
        FirewallStatus.Unknown => "Fw.StateUnknown",
        _ => "Fw.StateNone",
    });

    // ---------- lockout ----------

    /// <summary>
    /// True when the rules would refuse a new SSH connection from <paramref name="client"/> (this PC as the server sees it):
    /// a block rule for it, a port allow-list on the SSH port without it (the first one decides), or a whole-server
    /// allow-list without it.
    /// </summary>
    public static bool CutsSsh(FirewallConfig config, int sshPort, IPAddress client)
    {
        if (client.IsIPv4MappedToIPv6) client = client.MapToIPv4();
        var rules = config.Rules.Where(r => r.Enabled).ToList();
        bool Covers(FirewallRule r) => r.WholeServer ||
            r.Protocol.Split(',').Contains("tcp") && PortItems(r.Ports).Any(p => ForwardChains.Covers(p, sshPort.ToString()));
        bool Lists(FirewallRule r) => r.Sources.Any(s => TryNetwork(s, out var n) && n.Contains(client));
        if (rules.Any(r => r.Action == FirewallAction.Block && Covers(r) && Lists(r))) return true;
        if (rules.FirstOrDefault(r => r.Action == FirewallAction.AllowOnly && !r.WholeServer && Covers(r)) is { } first) return !Lists(first);
        var whole = rules.Where(r => r.Action == FirewallAction.AllowOnly && r.WholeServer).ToList();
        return whole.Count > 0 && !whole.Any(Lists);
    }

    // ---------- reading back ----------

    /// <summary>For the inventory: INPUT (its policy line tells that it ran as root) and the Quaykeep chain.</summary>
    public const string ReadScript = "iptables -S INPUT 2>&1; iptables -S " + InChain + " 2>/dev/null";

    /// <summary>The state from <see cref="ReadScript"/>'s output; null when it could not be read (no root, no iptables).</summary>
    public static FirewallState? ParseState(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        if (!lines.Any(l => l.StartsWith("-P INPUT ", StringComparison.Ordinal))) return null;
        var state = new FirewallState { Installed = lines.Contains("-A INPUT -j " + InChain) };
        foreach (var l in lines.Where(l => l.StartsWith("-A " + InChain + " ", StringComparison.Ordinal)))
        {
            var t = IptablesParser.Tokenize(l);
            var i = t.IndexOf("--comment");
            if (i < 0 || i + 1 >= t.Count) continue;
            var c = t[i + 1];
            if (c.StartsWith(HashPrefix, StringComparison.Ordinal)) state.Hash = c[HashPrefix.Length..];
            else if (c.StartsWith(CommentPrefix, StringComparison.Ordinal) && !state.RuleIds.Contains(c[CommentPrefix.Length..]))
                state.RuleIds.Add(c[CommentPrefix.Length..]);
        }
        return state;
    }

    // ---------- scripts ----------

    /// <summary>Shared by every script and by the files it leaves on the server (revert.sh, firewall-apply.sh).</summary>
    private const string Prologue = """
        D=/run/quaykeep-fw
        E=/etc/quaykeep
        have6(){ command -v ip6tables >/dev/null 2>&1 && ip6tables -S INPUT >/dev/null 2>&1; }
        # puts the jump to a Quaykeep chain first in a chain (moved up when something was inserted above it)
        top(){ # iptables|ip6tables chain target
          n=$($1 -S "$2" 2>/dev/null | awk -v r="-A $2 -j $3" '$0 == r { print NR - 1; exit }')
          [ "$n" = 1 ] && return 0
          $1 -I "$2" 1 -j "$3" || return 1
          if [ -n "$n" ]; then $1 -D "$2" $((n + 1)); fi
          return 0
        }
        hook(){ # iptables|ip6tables
          top "$1" INPUT QK-IN && top "$1" FORWARD QK-FWD || return 1
          # Docker keeps a DOCKER-USER chain that exists before it starts (at boot this runs first)
          if [ "$1" = iptables ] && command -v docker >/dev/null 2>&1 && ! iptables -S DOCKER-USER >/dev/null 2>&1; then
            iptables -N DOCKER-USER || return 1
          fi
          if $1 -S DOCKER-USER >/dev/null 2>&1; then top "$1" DOCKER-USER QK-FWD || return 1; fi
          return 0
        }
        unhook(){ # iptables|ip6tables
          for c in INPUT:QK-IN FORWARD:QK-FWD DOCKER-USER:QK-FWD; do
            while $1 -D "${c%%:*}" -j "${c#*:}" 2>/dev/null; do :; done
          done
          for c in QK-IN QK-FWD; do $1 -F "$c" 2>/dev/null; $1 -X "$c" 2>/dev/null; done
          return 0
        }
        disarm(){
          if command -v systemctl >/dev/null 2>&1; then
            systemctl stop quaykeep-fw-revert.timer quaykeep-fw-revert.service >/dev/null 2>&1
            systemctl reset-failed quaykeep-fw-revert.timer quaykeep-fw-revert.service >/dev/null 2>&1
          fi
          if [ -f "$D/timer.pid" ]; then kill "$(cat "$D/timer.pid")" 2>/dev/null; rm -f "$D/timer.pid"; fi
          return 0
        }

        """;

    private const string Revert = Prologue + """
        # written by Quaykeep: puts its firewall back as it was before the last change
        for t in iptables ip6tables; do
          [ -f "$D/prev.$t" ] || continue
          if grep -q '^:QK-IN ' "$D/prev.$t"; then "$t-restore" --noflush < "$D/prev.$t" && hook "$t"; else unhook "$t"; fi
        done
        rm -f "$D/revert.sh"
        echo QK_FW_REVERTED
        """;

    /// <summary>
    /// Saves what the chains hold now, arms a timer that puts it back after <paramref name="revertSeconds"/> seconds
    /// unless <see cref="Confirm"/> runs first, then loads the new rules. A failed load puts the old ones back at once.
    /// </summary>
    public static string Apply(FirewallConfig config, int revertSeconds)
    {
        Validate(config);
        return Prologue + $$"""
            if ! command -v iptables-restore >/dev/null 2>&1; then
              if command -v apt-get >/dev/null 2>&1; then
                DEBIAN_FRONTEND=noninteractive apt-get install -y -q iptables >/dev/null 2>&1 ||
                  { apt-get update -q >/dev/null 2>&1; DEBIAN_FRONTEND=noninteractive apt-get install -y -q iptables >/dev/null; }
              elif command -v dnf >/dev/null 2>&1; then
                dnf install -y -q iptables-nft >/dev/null 2>&1 || dnf install -y -q iptables >/dev/null
              fi
            fi
            command -v iptables-restore >/dev/null 2>&1 || { echo "iptables is missing and could not be installed" >&2; exit 3; }
            mkdir -p "$D" && chmod 700 "$D" || exit 4
            # 1. the way back: what the Quaykeep chains hold now
            for t in iptables ip6tables; do
              if [ "$t" = ip6tables ] && ! have6; then continue; fi
              { echo '*filter'; "$t-save" -t filter 2>/dev/null | grep -E '^:QK-(IN|FWD) |^-A QK-(IN|FWD) '; echo COMMIT; } > "$D/prev.$t"
            done
            cat > "$D/revert.sh" <<'QK_EOF'
            {{Revert}}
            QK_EOF
            # 2. the timer that puts it back unless Quaykeep confirms that a new login works
            disarm
            if command -v systemd-run >/dev/null 2>&1 && [ -d /run/systemd/system ] &&
              systemd-run --quiet --unit=quaykeep-fw-revert --on-active={{revertSeconds}} /bin/sh "$D/revert.sh"; then :
            else
              nohup sh -c "sleep {{revertSeconds}}; [ -f $D/revert.sh ] && sh $D/revert.sh" >/dev/null 2>&1 &
              echo $! > "$D/timer.pid"
            fi
            # 3. the new rules
            cat > "$D/new.iptables" <<'QK_EOF'
            {{Payload(config, v6: false).TrimEnd('\n')}}
            QK_EOF
            cat > "$D/new.ip6tables" <<'QK_EOF'
            {{Payload(config, v6: true).TrimEnd('\n')}}
            QK_EOF
            if iptables-restore --noflush < "$D/new.iptables" && hook iptables &&
              { ! have6 || { ip6tables-restore --noflush < "$D/new.ip6tables" && hook ip6tables; }; }; then
              echo QK_FW_APPLIED
            else
              echo "The rules could not be loaded; the previous ones are back" >&2
              sh "$D/revert.sh" >/dev/null 2>&1
              disarm
              exit 5
            fi
            """;
    }

    /// <summary>A new login worked: stops the timer and saves the rules so that they come back after a reboot.</summary>
    public const string Confirm = Prologue + """
        disarm
        rm -f "$D/revert.sh"
        mkdir -p "$E" || exit 4
        for t in iptables ip6tables; do
          if [ -f "$D/new.$t" ]; then cp "$D/new.$t" "$E/firewall.$t"; else rm -f "$E/firewall.$t"; fi
        done
        cat > "$E/firewall-apply.sh" <<'QK_EOF'
        """ + "\n" + Prologue + """
        # written by Quaykeep: its firewall chains and their hooks, at boot (quaykeep-firewall.service)
        for t in iptables ip6tables; do
          if [ "$t" = ip6tables ] && ! have6; then continue; fi
          if [ -f "$E/firewall.$t" ]; then "$t-restore" --noflush < "$E/firewall.$t" && hook "$t"; fi
        done
        exit 0
        QK_EOF
        if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
          cat > /etc/systemd/system/quaykeep-firewall.service <<'QK_EOF'
        [Unit]
        Description=Quaykeep firewall rules
        DefaultDependencies=no
        Wants=network-pre.target
        Before=network-pre.target docker.service
        After=local-fs.target systemd-modules-load.service

        [Service]
        Type=oneshot
        RemainAfterExit=yes
        ExecStart=/bin/sh /etc/quaykeep/firewall-apply.sh

        [Install]
        WantedBy=multi-user.target
        QK_EOF
          if systemctl daemon-reload && systemctl enable quaykeep-firewall.service >/dev/null 2>&1; then echo QK_FW_PERSIST=systemd; else echo QK_FW_PERSIST=none; fi
        else
          echo QK_FW_PERSIST=none
        fi
        echo QK_FW_CONFIRMED
        """;

    /// <summary>Puts the previous rules back now (the new login failed) and stops the timer.</summary>
    public const string RevertNow = Prologue + """
        if [ -f "$D/revert.sh" ]; then sh "$D/revert.sh"; fi
        disarm
        """;

    /// <summary>Removes Quaykeep's firewall from the server: the hooks, the chains, the boot unit and its files.</summary>
    public const string TurnOff = Prologue + """
        disarm
        for t in iptables ip6tables; do
          if command -v "$t" >/dev/null 2>&1; then unhook "$t"; fi
        done
        if command -v systemctl >/dev/null 2>&1; then
          systemctl disable quaykeep-firewall.service >/dev/null 2>&1
          rm -f /etc/systemd/system/quaykeep-firewall.service
          systemctl daemon-reload >/dev/null 2>&1
        fi
        rm -rf "$E/firewall.iptables" "$E/firewall.ip6tables" "$E/firewall-apply.sh" "$D"
        rmdir "$E" 2>/dev/null
        echo QK_FW_OFF
        """;

    public static string? PersistResult(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(PersistMarker, StringComparison.Ordinal))?[PersistMarker.Length..];
}
