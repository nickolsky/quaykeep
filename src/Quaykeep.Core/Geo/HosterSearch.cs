using Quaykeep.Core.Models;

namespace Quaykeep.Core.Geo;

/// <summary>A web search for the hoster's control panel ("Find … control panel" in a server's menu).</summary>
public static class HosterSearch
{
    /// <summary>
    /// "Hetzner Online GmbH hetzner.com control panel login"; null while the hoster is unknown. The domain is a search
    /// word, not a site: filter, because panels often live elsewhere (console.hetzner.cloud, robot.your-server.de).
    /// </summary>
    public static string? Query(ServerFacts? facts)
    {
        if (facts?.Hoster is not { } hoster) return null;
        // the domain belongs to the AS organization: only when that is who the hoster is
        var domain = facts.Geo is { Domain: { } d } g && hoster == (g.Org ?? g.Isp) ? " " + d : "";
        return $"{hoster}{domain} control panel login";
    }

    public static string Url(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
}
