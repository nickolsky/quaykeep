using System.Text.Json;
using Quaykeep.Core.Geo;
using Quaykeep.Core.Models;
using Quaykeep.Core.Monitoring;

namespace Quaykeep.Tests;

public class WhoisTraceTests
{
    // shaped like RIPE's answer: the organization and its maintainer are both registrants, the abuse contact is its own entity
    private const string Ripe = """
        {
          "objectClassName": "ip network", "handle": "203.0.113.0 - 203.0.113.255", "name": "EXAMPLE-NET", "country": "DE",
          "port43": "whois.ripe.net", "startAddress": "203.0.113.0", "endAddress": "203.0.113.255",
          "cidr0_cidrs": [{ "v4prefix": "203.0.113.0", "length": 24 }],
          "remarks": [{ "description": ["Example Hosting GmbH", "Falkenstein"] }],
          "events": [{ "eventAction": "registration", "eventDate": "2014-03-17T12:15:57Z" }, { "eventAction": "last changed", "eventDate": "2025-01-02T03:04:05Z" }],
          "entities": [
            { "handle": "EX-MNT", "roles": ["registrant"], "vcardArray": ["vcard", [["fn", {}, "text", "EX-MNT"], ["kind", {}, "text", "individual"]]] },
            { "handle": "ORG-EX1-RIPE", "roles": ["registrant"], "vcardArray": ["vcard", [["fn", {}, "text", "Example Hosting GmbH"], ["kind", {}, "text", "org"],
                ["adr", { "label": "Industriestr. 1\n91710 Example\nGERMANY" }, "text", ["", "", "", "", "", "", ""]]]] },
            { "handle": "AB1-RIPE", "roles": ["abuse"], "vcardArray": ["vcard", [["fn", {}, "text", "Abuse"], ["email", { "type": "abuse" }, "text", "abuse@example.net"]]] }
          ]
        }
        """;

    // shaped like ARIN's: the abuse contact sits inside the organization, no description
    private const string Arin = """
        {
          "objectClassName": "ip network", "handle": "NET-198-51-100-0-1", "name": "EXAMPLE", "port43": "whois.arin.net",
          "startAddress": "198.51.100.0", "endAddress": "198.51.100.255",
          "entities": [
            { "handle": "EXMPL", "roles": ["registrant"], "vcardArray": ["vcard", [["fn", {}, "text", "Example LLC"], ["kind", {}, "text", "org"]]],
              "entities": [{ "handle": "ABUSE1-ARIN", "roles": ["abuse"], "vcardArray": ["vcard", [["fn", {}, "text", "Abuse"], ["email", {}, "text", "network-abuse@example.com"]]] }] }
          ]
        }
        """;

    [Fact]
    public void Rdap_Records_Give_Owner_Network_Abuse_And_Dates()
    {
        var w = Whois.Parse(JsonDocument.Parse(Ripe).RootElement, "203.0.113.9")!;
        Assert.Equal("RIPE NCC", w.Registry);
        Assert.Equal("203.0.113.0/24", w.Network);
        Assert.Equal("203.0.113.0 - 203.0.113.255", w.Range);
        Assert.Equal("EXAMPLE-NET", w.NetName);
        Assert.Equal("Example Hosting GmbH", w.Owner); // the organization, not its maintainer object
        Assert.Equal("ORG-EX1-RIPE", w.OwnerHandle);
        Assert.Equal("Industriestr. 1, 91710 Example, GERMANY", w.Address);
        Assert.Equal("abuse@example.net", w.AbuseEmail);
        Assert.Equal("Example Hosting GmbH\nFalkenstein", w.Description);
        Assert.Equal(2014, w.Registered!.Value.Year);
        Assert.Equal(2025, w.Changed!.Value.Year);

        var a = Whois.Parse(JsonDocument.Parse(Arin).RootElement, "198.51.100.7")!;
        Assert.Equal(("ARIN", "Example LLC", "network-abuse@example.com"), (a.Registry, a.Owner, a.AbuseEmail));
        Assert.Equal("198.51.100.0 - 198.51.100.255", a.Network); // no cidr0 extension: the range

        Assert.Null(Whois.Parse(JsonDocument.Parse("""{"objectClassName":"autnum"}""").RootElement, "x"));
    }

    [Fact]
    public void Hoster_Is_The_Network_Operator_Before_The_Block_Owner()
    {
        var f = new ServerFacts
        {
            Geo = new GeoInfo { Isp = "Cloudflare, Inc.", Org = "Cloudflare, Inc.", Domain = "cloudflare.com", Asn = 13335 },
            Whois = new WhoisInfo { Owner = "APNIC Research and Development" },
        };
        Assert.Equal("Cloudflare, Inc.", f.Hoster);
        Assert.Equal("Cloudflare, Inc. cloudflare.com control panel login", HosterSearch.Query(f));
        Assert.Contains("q=Cloudflare%2C%20Inc.", HosterSearch.Url(HosterSearch.Query(f)!));
        Assert.Equal("APNIC Research and Development", new ServerFacts { Whois = f.Whois }.Hoster);
        Assert.Equal("APNIC Research and Development control panel login", HosterSearch.Query(new ServerFacts { Whois = f.Whois }));
        Assert.Null(HosterSearch.Query(new ServerFacts()));
        Assert.Equal(24940, GeoIpService.AsNumber("AS24940 Hetzner Online GmbH"));
        Assert.Equal("Hetzner Online GmbH", GeoIpService.AsName("AS24940 Hetzner Online GmbH"));
        Assert.Null(GeoIpService.AsNumber(""));
    }

    [Fact]
    public void Traceroute_And_Tracepath_Output_Become_Hops()
    {
        var t = TraceRoute.ParseRemote("""
            @@sshm:traceroute
            traceroute to 198.51.100.7 (198.51.100.7), 30 hops max, 60 byte packets
             1  10.0.0.1  0.412 ms  0.380 ms  0.355 ms
             2  * * *
             3  203.0.113.1  5.1 ms 203.0.113.2  6.0 ms *
             4  198.51.100.7  12.5 ms !H  12.1 ms  12.3 ms
            """, "198.51.100.7")!;
        Assert.Equal([1, 2, 3, 4], t.Select(h => h.Number));
        Assert.True(t[1].Silent);
        Assert.Equal("203.0.113.1", t[2].Address); // the first router that answered
        Assert.Equal([5.1, 6.0, null], t[2].Rtts);
        Assert.Equal(12.1, t[3].Best);
        Assert.True(t[3].Reached);

        var p = TraceRoute.ParseRemote("""
            @@sshm:tracepath
             1?: [LOCALHOST]                      pmtu 1500
             1:  10.0.0.1                                              0.083ms
             1:  10.0.0.1                                              0.030ms
             2:  no reply
             3:  198.51.100.7                                          5.2ms reached
                 Resume: pmtu 1500 hops 3 back 3
            """, "198.51.100.7")!;
        Assert.Equal(3, p.Count);
        Assert.Equal([0.083, 0.030], p[0].Rtts);
        Assert.True(p[1].Silent);
        Assert.True(p[2].Reached);

        Assert.Null(TraceRoute.ParseRemote("@@sshm:none\n", "x"));
        Assert.Throws<ArgumentException>(() => TraceRoute.RemoteScript("1.2.3.4; rm -rf /"));
        Assert.Contains("'example.com'", TraceRoute.RemoteScript("example.com"));
    }

    private static TraceHop Hop(int n, string? ip, params double?[] rtts) => new() { Number = n, Address = ip, Rtts = [.. rtts] };

    private static GeoInfo G(string org, int asn, string city, string cc, double lat, double lon) =>
        new() { Org = org, Asn = asn, City = city, CountryCode = cc, Latitude = lat, Longitude = lon };

    [Fact]
    public void The_Route_Is_Read_By_Provider_Country_And_Delay()
    {
        var hops = new List<TraceHop>
        {
            Hop(1, "192.168.1.1", 1, 1, 1),
            Hop(2, "203.0.113.1", 5, 6, 5),
            Hop(3, "203.0.113.2", 60, 7, 8), // slow to answer itself: the next hop tells the real delay
            Hop(4, null, null, null, null),
            Hop(5, "198.51.100.1", 40, 41, 40),
            Hop(6, "198.51.100.2", 41, 42, 41),
            Hop(7, "192.0.2.7", 42, 42, 43),
        };
        hops[^1].Reached = true;
        var geo = new Dictionary<string, GeoInfo>
        {
            ["203.0.113.1"] = G("Home ISP", 64500, "Moscow", "RU", 55.75, 37.62),
            ["203.0.113.2"] = G("Home ISP", 64500, "Moscow", "RU", 55.75, 37.62),
            ["198.51.100.1"] = G("Backbone", 64501, "Frankfurt am Main", "DE", 50.11, 8.68),
            // the same backbone, "in Los Angeles", 1 ms after Frankfurt: impossible from Moscow in 36 ms
            ["198.51.100.2"] = G("Backbone", 64501, "Los Angeles", "US", 34.05, -118.24),
            ["192.0.2.7"] = G("Example Hosting", 64502, "Falkenstein", "DE", 50.48, 12.37),
        };
        var before = L.Language;
        L.Language = L.English;
        try
        {
            Check(hops, geo);
        }
        finally
        {
            L.Language = before;
        }
    }

    private static void Check(List<TraceHop> hops, Dictionary<string, GeoInfo> geo)
    {
        var r = TraceAnalysis.Analyze(hops, geo);

        Assert.Equal(["Local network", "Home ISP", "Backbone", "Example Hosting"], r.Segments.Select(s => s.Provider));
        Assert.Equal(["Frankfurt am Main, DE"], r.Segments[2].Places); // Los Angeles is left out
        Assert.True(r.HopNotes.ContainsKey(6));
        Assert.False(r.HopNotes.ContainsKey(5));
        Assert.Contains(r.Notes, n => n.Contains("RU → DE"));
        Assert.Contains(r.Notes, n => n.Contains("+33") && n.Contains("hop 3") && n.Contains("hop 5")); // 7 → 40 ms
        Assert.Contains(r.Notes, n => n.Contains("1") && n.Contains("did not answer") && n.StartsWith("Hops"));
        Assert.Contains(r.Notes, n => n.StartsWith("Reached in 7 hops"));
        Assert.StartsWith("Local network → Home ISP (Moscow, RU) → Backbone (Frankfurt am Main, DE) → Example Hosting", r.Notes[0]);

        var cut = TraceAnalysis.Analyze([Hop(1, "192.168.1.1", 1), Hop(2, null, [null])], geo);
        Assert.Contains(cut.Notes, n => n.Contains("known up to hop 1"));
        Assert.Contains(TraceAnalysis.Analyze([Hop(1, null, [null])], geo).Notes, n => n.StartsWith("No hop answered"));
        var tunnel = Hop(1, "192.0.2.7", 0, 0, 0);
        tunnel.Reached = true;
        Assert.Contains(TraceAnalysis.Analyze([tunnel], geo).Notes, n => n.Contains("VPN or proxy"));
        Assert.DoesNotContain(r.Notes, n => n.Contains("VPN or proxy"));
    }
}
