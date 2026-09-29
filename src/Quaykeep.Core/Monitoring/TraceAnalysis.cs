using System.Net;
using Quaykeep.Core.Geo;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Monitoring;

/// <summary>A stretch of the route run by one network (AS): its hops, where they are, and the delay at its ends.</summary>
public sealed record TraceSegment(string Provider, int? Asn, int FirstHop, int LastHop, List<string> Places, double? EnterMs, double? ExitMs);

/// <param name="HopNotes">Per hop number: why its location or delay stands out.</param>
public sealed record TraceReport(List<TraceSegment> Segments, List<string> Notes, Dictionary<int, string> HopNotes);

/// <summary>
/// Reads a traceroute with the hops' networks and locations: which providers carry the traffic and through which
/// countries, where the delay is added, and which locations cannot be right for how fast the hop answers.
/// </summary>
public static class TraceAnalysis
{
    /// <summary>Light in fibre covers about 200 km per ms, so a round trip needs at least 1 ms per 100 km.</summary>
    private const double KmPerRttMs = 100;
    /// <summary>Slack for queueing and for GeoIP pointing at a city nearby.</summary>
    private const double SlackMs = 3;
    private const double MinJumpMs = 15;

    public static TraceReport Analyze(IReadOnlyList<TraceHop> hops, IReadOnlyDictionary<string, GeoInfo> geo)
    {
        var notes = new List<string>();
        var hopNotes = new Dictionary<int, string>();
        GeoInfo? Geo(TraceHop h) => h.Address != null && geo.TryGetValue(h.Address, out var g) ? g : null;
        var answered = hops.Where(h => !h.Silent).ToList();
        if (answered.Count == 0)
        {
            notes.Add(L.Get("Trace.NothingAnswered"));
            return new TraceReport([], notes, hopNotes);
        }

        // the delay to a hop can't be less than to a hop after it: a router answering slowly itself only looks far
        var floor = new Dictionary<int, double>();
        double? next = null;
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            if (hops[i].Best is { } b) next = next is { } n ? Math.Min(n, b) : b;
            if (next is { } f && !hops[i].Silent) floor[hops[i].Number] = f;
        }

        // where a location is impossible for the delay, it is left out of the picture
        var origin = answered.Select(h => (Hop: h, Geo: Geo(h))).FirstOrDefault(x => x.Geo?.Latitude != null);
        var trusted = new HashSet<int>();
        foreach (var h in answered)
        {
            var g = Geo(h);
            if (g?.Latitude == null || origin.Geo == null) continue;
            if (!floor.TryGetValue(h.Number, out var rtt) || !floor.TryGetValue(origin.Hop.Number, out var start))
            {
                trusted.Add(h.Number);
                continue;
            }
            var km = Distance(origin.Geo, g);
            var need = km / KmPerRttMs;
            if (rtt - start + SlackMs < need)
                hopNotes[h.Number] = L.F("Trace.TooFast", Place(g), Math.Round(km), Math.Round(need), Math.Round(Math.Max(0, rtt - start)));
            else trusted.Add(h.Number);
        }

        // stretches of the route by network; private hops belong to whoever is around them
        var segments = new List<TraceSegment>();
        foreach (var h in answered)
        {
            var g = Geo(h);
            var isPrivate = h.Address != null && IPAddress.TryParse(h.Address, out var ip) && !GeoIpService.IsPublic(ip);
            var provider = isPrivate ? null : g?.Org ?? g?.Isp ?? L.Get("Trace.UnknownNetwork");
            var place = g != null && trusted.Contains(h.Number) ? Place(g) : null;
            floor.TryGetValue(h.Number, out var ms);
            var last = segments.Count > 0 ? segments[^1] : null;
            if (last != null && (provider == null || Same(last, g?.Asn, provider)))
            {
                var places = place != null && !last.Places.Contains(place) ? [.. last.Places, place] : last.Places;
                segments[^1] = last with { LastHop = h.Number, Places = places, ExitMs = floor.ContainsKey(h.Number) ? ms : last.ExitMs };
            }
            else
            {
                segments.Add(new TraceSegment(provider ?? L.Get("Trace.LocalNetwork"), g?.Asn, h.Number, h.Number,
                    place != null ? [place] : [], floor.ContainsKey(h.Number) ? ms : null, floor.ContainsKey(h.Number) ? ms : null));
            }
        }

        notes.Add(string.Join(" → ", segments.Select(s => s.Places.Count > 0 ? $"{s.Provider} ({string.Join(", ", s.Places)})" : s.Provider)));

        var countries = answered.Where(h => trusted.Contains(h.Number)).Select(Geo).Select(g => g?.CountryCode).OfType<string>().ToList();
        var path = new List<string>();
        foreach (var c in countries)
            if (path.Count == 0 || path[^1] != c) path.Add(c);
        if (path.Count > 1) notes.Add(L.F("Trace.Countries", string.Join(" → ", path)));
        else if (path.Count == 1 && answered.Count > 1) notes.Add(L.F("Trace.OneCountry", path[0]));

        // the biggest step in the delay: usually the long-distance link
        (TraceHop From, TraceHop To, double Ms)? jump = null;
        for (var i = 1; i < answered.Count; i++)
        {
            if (!floor.TryGetValue(answered[i - 1].Number, out var a) || !floor.TryGetValue(answered[i].Number, out var b)) continue;
            if (b - a >= MinJumpMs && (jump == null || b - a > jump.Value.Ms)) jump = (answered[i - 1], answered[i], b - a);
        }
        if (jump is { } j)
            notes.Add(L.F("Trace.Jump", Math.Round(j.Ms), j.From.Number, Where(j.From, Geo(j.From), trusted), j.To.Number, Where(j.To, Geo(j.To), trusted)));

        if (hopNotes.Count > 0) notes.Add(L.F("Trace.TooFastSummary", hopNotes.Count));
        var silent = hops.Count(h => h.Silent && hops.Any(x => x.Number > h.Number && !x.Silent));
        if (silent > 0) notes.Add(L.F("Trace.SilentHops", silent));

        var end = hops[^1];
        // nothing between this PC and a public address: a VPN or proxy answers for it and hides the real route
        if (end.Reached && answered.Count == 1 && end.Number == 1 && end.Address != null &&
            IPAddress.TryParse(end.Address, out var dest) && GeoIpService.IsPublic(dest))
            notes.Add(L.Get("Trace.Tunnel"));
        if (end.Reached)
            notes.Add(L.F("Trace.Reached", end.Number, end.Best is { } e ? Math.Round(e).ToString() : "?"));
        else notes.Add(L.F("Trace.NotReached", answered[^1].Number));
        return new TraceReport(segments, notes, hopNotes);
    }

    private static bool Same(TraceSegment s, int? asn, string provider) =>
        asn != null && s.Asn != null ? asn == s.Asn : string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase);

    private static string Where(TraceHop h, GeoInfo? g, HashSet<int> trusted) =>
        g != null && trusted.Contains(h.Number) ? $"{h.Address}, {Place(g)}" : h.Address ?? "*";

    /// <summary>"Frankfurt am Main, DE".</summary>
    public static string Place(GeoInfo g) =>
        string.Join(", ", new[] { g.City, g.CountryCode ?? g.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>Great-circle distance in km.</summary>
    public static double Distance(GeoInfo a, GeoInfo b)
    {
        const double R = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var (lat1, lat2) = (Rad(a.Latitude!.Value), Rad(b.Latitude!.Value));
        var dLat = lat2 - lat1;
        var dLon = Rad(b.Longitude!.Value - a.Longitude!.Value);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
