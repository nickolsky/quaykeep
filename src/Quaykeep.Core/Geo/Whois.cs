using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Quaykeep.Core.Models;

namespace Quaykeep.Core.Geo;

/// <summary>
/// Whois of an IP: RDAP (the registries' JSON whois; rdap.org sends the query on to RIPE, ARIN, APNIC, LACNIC or
/// AFRINIC) for the stored record, and the classic text whois on port 43 for the full record in the whois window.
/// </summary>
public static class Whois
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const int MaxRaw = 128 * 1024;

    static Whois()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Quaykeep/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/rdap+json, application/json");
    }

    /// <returns>null when the registry has no record (or answered with something else).</returns>
    public static async Task<WhoisInfo?> RdapAsync(string ip, CancellationToken ct = default)
    {
        using var r = await Http.GetAsync($"https://rdap.org/ip/{Uri.EscapeDataString(ip)}", ct);
        if (!r.IsSuccessStatusCode) return null;
        var json = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        return Parse(json, ip);
    }

    /// <summary>The fields Quaykeep keeps from an RDAP "ip network" object.</summary>
    public static WhoisInfo? Parse(JsonElement net, string ip)
    {
        if (net.ValueKind != JsonValueKind.Object || Str(net, "objectClassName") is not (null or "ip network")) return null;
        var w = new WhoisInfo
        {
            Ip = ip, Handle = Str(net, "handle"), NetName = Str(net, "name"), Country = Str(net, "country"),
            Port43 = Str(net, "port43"), Updated = DateTime.Now,
        };
        w.Registry = RegistryOf(w.Port43);
        if (Str(net, "startAddress") is { } start && Str(net, "endAddress") is { } end) w.Range = $"{start} - {end}";
        if (net.TryGetProperty("cidr0_cidrs", out var cidrs) && cidrs.ValueKind == JsonValueKind.Array)
        {
            var list = cidrs.EnumerateArray()
                .Select(c => (Str(c, "v4prefix") ?? Str(c, "v6prefix")) is { } p && c.TryGetProperty("length", out var l) ? $"{p}/{l}" : null)
                .OfType<string>().ToList();
            if (list.Count > 0) w.Network = string.Join(", ", list);
        }
        w.Network ??= w.Range;

        var entities = Entities(net).ToList();
        // RIPE lists the organization and its maintainer object as registrants: the organization first
        var owner = entities.Where(e => Roles(e).Contains("registrant")).OrderBy(e => VcardText(e, "kind") == "org" ? 0 : 1).FirstOrDefault();
        if (owner.ValueKind == JsonValueKind.Object)
        {
            w.Owner = VcardText(owner, "fn");
            w.OwnerHandle = Str(owner, "handle");
            w.Address = VcardAddress(owner);
        }
        var abuse = entities.FirstOrDefault(e => Roles(e).Contains("abuse"));
        if (abuse.ValueKind == JsonValueKind.Object) w.AbuseEmail = VcardText(abuse, "email");
        w.AbuseEmail ??= entities.Select(e => VcardText(e, "email")).FirstOrDefault(m => m?.Contains("abuse", StringComparison.OrdinalIgnoreCase) == true);

        if (net.TryGetProperty("remarks", out var remarks) && remarks.ValueKind == JsonValueKind.Array)
        {
            var lines = remarks.EnumerateArray()
                .Where(r => Str(r, "title") is null or "description" or "Registration Comments")
                .SelectMany(r => r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                    : [])
                .Where(x => x.Trim().Length > 0).Take(6).ToList();
            if (lines.Count > 0) w.Description = string.Join("\n", lines);
        }
        // RIPE puts the organization in the description when the network has no registrant of its own
        w.Owner ??= w.Description?.Split('\n')[0].Trim();

        if (net.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            foreach (var e in events.EnumerateArray())
            {
                if (!DateTime.TryParse(Str(e, "eventDate"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) continue;
                switch (Str(e, "eventAction"))
                {
                    case "registration": w.Registered = when; break;
                    case "last changed": w.Changed = when; break;
                }
            }
        return w;
    }

    public static string? RegistryOf(string? port43) => port43?.ToLowerInvariant() switch
    {
        null => null,
        var h when h.Contains("ripe") => "RIPE NCC",
        var h when h.Contains("arin") => "ARIN",
        var h when h.Contains("apnic") => "APNIC",
        var h when h.Contains("lacnic") => "LACNIC",
        var h when h.Contains("afrinic") => "AFRINIC",
        var h => h,
    };

    /// <summary>
    /// The text record from the registry's whois server (port 43; <paramref name="server"/> from RDAP, else IANA's,
    /// which names the registry to ask).
    /// </summary>
    public static async Task<string> RawAsync(string ip, string? server, CancellationToken ct = default)
    {
        server ??= "whois.iana.org";
        var text = await QueryAsync(server, Query(server, ip), ct);
        if (server == "whois.iana.org" &&
            text.Split('\n').FirstOrDefault(l => l.StartsWith("refer:", StringComparison.OrdinalIgnoreCase)) is { } refer)
        {
            server = refer[6..].Trim();
            text = await QueryAsync(server, Query(server, ip), ct);
        }
        return $"% {server}\n\n{text}";
    }

    /// <summary>ARIN answers a bare IP with a list of matches; "n" asks for the network itself.</summary>
    private static string Query(string server, string ip) => server.Contains("arin", StringComparison.OrdinalIgnoreCase) ? "n + " + ip : ip;

    private static async Task<string> QueryAsync(string server, string query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server, 43, timeout.Token);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(query + "\r\n"), timeout.Token);
        var buffer = new byte[8192];
        using var all = new MemoryStream();
        int n;
        while (all.Length < MaxRaw && (n = await stream.ReadAsync(buffer, timeout.Token)) > 0) all.Write(buffer, 0, n);
        return Encoding.UTF8.GetString(all.ToArray()).Replace("\r\n", "\n").Trim();
    }

    // ---------- RDAP helpers ----------

    /// <summary>Entities at every depth (ARIN keeps the abuse contact inside the organization).</summary>
    private static IEnumerable<JsonElement> Entities(JsonElement e)
    {
        if (!e.TryGetProperty("entities", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in list.EnumerateArray())
        {
            yield return x;
            foreach (var y in Entities(x)) yield return y;
        }
    }

    private static List<string> Roles(JsonElement e) =>
        e.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    /// <summary>jCard: ["vcard", [[name, params, type, value], …]].</summary>
    private static IEnumerable<JsonElement> Vcard(JsonElement e)
    {
        if (!e.TryGetProperty("vcardArray", out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2) yield break;
        var props = v[1];
        if (props.ValueKind != JsonValueKind.Array) yield break;
        foreach (var p in props.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 4) yield return p;
    }

    private static string? VcardText(JsonElement e, string name) =>
        Vcard(e).Where(p => p[0].GetString() == name && p[3].ValueKind == JsonValueKind.String)
            .Select(p => p[3].GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();

    private static string? VcardAddress(JsonElement e)
    {
        foreach (var p in Vcard(e).Where(p => p[0].GetString() == "adr"))
        {
            if (p[1].ValueKind == JsonValueKind.Object && Str(p[1], "label") is { } label) return label.Replace("\r", "").Trim().Replace("\n", ", ");
            if (p[3].ValueKind == JsonValueKind.Array)
            {
                var parts = p[3].EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim())
                    .Where(x => x.Length > 0).ToList();
                if (parts.Count > 0) return string.Join(", ", parts);
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
        p.GetString() is { Length: > 0 } s ? s.Trim() : null;
}
