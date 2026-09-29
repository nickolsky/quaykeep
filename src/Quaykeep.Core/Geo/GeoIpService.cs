using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Quaykeep.Core.Models;
using Quaykeep.Core.Storage;

namespace Quaykeep.Core.Geo;

/// <summary>
/// Server location and network (AS, organization) from an online GeoIP service (ipwho.is, falling back to ip-api.com),
/// and the registry's whois record of its address (<see cref="Whois"/>).
/// </summary>
public sealed class GeoIpService(VaultService vault, SettingsService settings)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly SemaphoreSlim _gate = new(2);

    static GeoIpService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Quaykeep/1.0");

    public bool Enabled => settings.Settings.GeoIpEnabled;

    /// <summary>Looks up the location if it is unknown, outdated, in another language or the IP changed.</summary>
    public Task RefreshAsync(Guid serverId, bool force = false)
    {
        if (!Enabled) return Task.CompletedTask;
        return _running.GetOrAdd(serverId, id => Task.Run(async () =>
        {
            try
            {
                await RunAsync(id, force);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException
                                           or JsonException or VaultLockedException or NotSupportedException)
            {
            }
            finally
            {
                _running.TryRemove(id, out _);
            }
        }));
    }

    private async Task RunAsync(Guid id, bool force)
    {
        if (!vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == id) is { } s ? (s.Host, s.Facts?.Geo, s.Facts?.Whois) : default,
                out var info) || info.Host == null)
            return;
        var ip = await ResolveAsync(info.Host);
        if (ip == null) return;
        var lang = L.Language;
        var geo = info.Geo;
        // records from before the AS and organization were kept (Org == null) are read again once
        var needGeo = force || geo == null || geo.Ip != ip || geo.Language != lang || geo.Org == null || DateTime.Now - geo.Updated >= MaxAge;
        // whois changes rarely: once per address, and when asked
        var needWhois = force || info.Whois == null || info.Whois.Ip != ip;
        if (!needGeo && !needWhois) return;

        await _gate.WaitAsync();
        try
        {
            if (needGeo && await LookupAsync(ip, lang) is { } result) vault.UpdateFacts(id, f => f.Geo = result);
            if (needWhois)
            {
                try
                {
                    if (await Whois.RdapAsync(ip) is { } whois) vault.UpdateFacts(id, f => f.Whois = whois);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<string?> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return IsPublic(literal) ? literal.ToString() : null;
        var addresses = await Dns.GetHostAddressesAsync(host);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        return ip != null && IsPublic(ip) ? ip.ToString() : null;
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || b[0] == 0);
    }

    public static async Task<GeoInfo?> LookupAsync(string ip, string lang)
    {
        try
        {
            var r = await Http.GetFromJsonAsync<JsonElement>($"https://ipwho.is/{ip}?lang={lang}");
            if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var c = r.TryGetProperty("connection", out var conn) ? conn : default;
                return new GeoInfo
                {
                    Ip = ip,
                    Country = Str(r, "country"),
                    CountryCode = Str(r, "country_code"),
                    Region = Str(r, "region"),
                    City = Str(r, "city"),
                    Isp = Str(c, "isp") ?? Str(c, "org"),
                    Org = Str(c, "isp") ?? Str(c, "org"), // isp is the AS operator (Cloudflare); org the block's registrant (APNIC R&D)
                    Asn = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("asn", out var asn) && asn.TryGetInt32(out var n) ? n : null,
                    Domain = Str(c, "domain"),
                    Latitude = Num(r, "latitude"),
                    Longitude = Num(r, "longitude"),
                    Language = lang,
                    Updated = DateTime.Now,
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }

        var a = await Http.GetFromJsonAsync<JsonElement>($"http://ip-api.com/json/{ip}?fields={IpApiFields}&lang={lang}");
        return Str(a, "status") == "success" ? FromIpApi(a, ip, lang) : null;
    }

    private const string IpApiFields = "status,query,country,countryCode,regionName,city,lat,lon,isp,org,as";

    private static GeoInfo FromIpApi(JsonElement a, string ip, string lang) => new()
    {
        Ip = ip,
        Country = Str(a, "country"),
        CountryCode = Str(a, "countryCode"),
        Region = Str(a, "regionName"),
        City = Str(a, "city"),
        Isp = Str(a, "isp"),
        Org = AsName(Str(a, "as")) ?? Str(a, "org") ?? Str(a, "isp"),
        Asn = AsNumber(Str(a, "as")),
        Latitude = Num(a, "lat"),
        Longitude = Num(a, "lon"),
        Language = lang,
        Updated = DateTime.Now,
    };

    /// <summary>
    /// Many addresses at once (the hops of a traceroute): ip-api.com's batch lookup, up to 100 addresses a request.
    /// Private addresses are skipped. Returns what was found; nothing when GeoIP is off in the settings.
    /// </summary>
    public async Task<Dictionary<string, GeoInfo>> LookupManyAsync(IEnumerable<string> ips, CancellationToken ct = default)
    {
        var result = new Dictionary<string, GeoInfo>();
        if (!Enabled) return result;
        var lang = L.Language;
        var list = ips.Distinct().Where(i => IPAddress.TryParse(i, out var a) && IsPublic(a)).ToList();
        foreach (var chunk in list.Chunk(100))
        {
            using var r = await Http.PostAsJsonAsync($"http://ip-api.com/batch?fields={IpApiFields}&lang={lang}", chunk, ct);
            if (!r.IsSuccessStatusCode) break;
            var items = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (items.ValueKind != JsonValueKind.Array) break;
            foreach (var a in items.EnumerateArray())
                if (Str(a, "status") == "success" && Str(a, "query") is { } q) result[q] = FromIpApi(a, q, lang);
        }
        return result;
    }

    /// <summary>"AS24940 Hetzner Online GmbH" → 24940.</summary>
    public static int? AsNumber(string? asText) =>
        asText is { Length: > 2 } && asText.StartsWith("AS", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(asText[2..].Split(' ')[0], out var n) ? n : null;

    /// <summary>"AS24940 Hetzner Online GmbH" → "Hetzner Online GmbH".</summary>
    public static string? AsName(string? asText) =>
        asText?.IndexOf(' ') is > 0 and var i && asText[(i + 1)..].Trim() is { Length: > 0 } name ? name : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;
}
