using System.Text.RegularExpressions;

namespace Quaykeep.Core.Mcp;

/// <summary>
/// Which install-script results are credentials: passwords, keys and tokens by name, and login links by value (a VPN
/// link carries the client's id or password; a URL can carry user:password). They are hidden from agents below full
/// access, and from every agent when the user turns that on.
/// </summary>
public static partial class ScriptSecrets
{
    /// <summary>Schemes whose links are enough to log in: VPN clients import them as they are.</summary>
    private static readonly HashSet<string> LoginSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "vmess", "trojan", "ss", "ssr", "hysteria", "hysteria2", "hy2", "tuic", "wireguard", "wg", "awg", "amneziawg", "vpn",
    };

    [GeneratedRegex("PASSWORD|PASSWD|SECRET|TOKEN|PRIVATE|KEY", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    /// <summary>A public key is meant to be shared (XRAY_PUBKEY).</summary>
    [GeneratedRegex("PUBKEY|PUBLIC_?KEY", RegexOptions.IgnoreCase)]
    private static partial Regex PublicName();

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9+.\-]*)://\S+")]
    private static partial Regex Link();

    public static bool IsSecret(string key, string? value) =>
        SecretName().IsMatch(key) && !PublicName().IsMatch(key) || value != null && Link().Matches(value).Any(m => IsLoginLink(m.Value));

    /// <summary>A VPN link, or a URL with a user and password in it.</summary>
    public static bool IsLoginLink(string text)
    {
        var colon = text.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0) return false;
        if (LoginSchemes.Contains(text[..colon])) return true;
        var rest = text[(colon + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        return at > 0 && authority[..at].Contains(':'); // user:password@host
    }

    /// <summary>
    /// Script output with the hidden values and any login link taken out (scripts print their links and passwords too).
    /// </summary>
    public static string Redact(string text, IEnumerable<string> hiddenValues, string mark)
    {
        foreach (var v in hiddenValues.Where(v => v.Length >= 4).OrderByDescending(v => v.Length))
            text = text.Replace(v, mark, StringComparison.Ordinal);
        return Link().Replace(text, m => IsLoginLink(m.Value) ? mark : m.Value);
    }
}
