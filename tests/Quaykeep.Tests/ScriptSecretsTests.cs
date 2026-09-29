using System.Text.Json.Nodes;
using Quaykeep.Core.Mcp;
using Quaykeep.Core.Models;

namespace Quaykeep.Tests;

public class ScriptSecretsTests
{
    [Theory]
    [InlineData("FB_ADMIN_PASSWORD", "x", true)]
    [InlineData("AWG_KEY_phone", "[Interface]", true)]
    [InlineData("API_TOKEN", "abc", true)]
    [InlineData("XRAY_PUBKEY", "Zx1abc", false)] // meant to be shared
    [InlineData("VLESS_URL", "vless://6f1c3a52-uuid@203.0.113.9:443?security=reality#vpn", true)]
    [InlineData("HY2_URL", "hysteria2://secret@203.0.113.9:8443?sni=example.com", true)]
    [InlineData("AMNEZIA", "vpn://AAAAeJyrVkrLz1eyUkrKL8pLzUvOTM7PS1WqBQA", true)]
    [InlineData("DB_URL", "postgres://app:pa55@db:5432/app", true)] // user:password in the address
    [InlineData("SITE_URL", "https://example.com:8443/", false)]
    [InlineData("FB_URL", "https://admin@example.com/", false)] // a user alone is not a login
    [InlineData("FTP_PORT", "2121", false)]
    public void Passwords_Keys_And_Login_Links_Are_Secret(string key, string value, bool secret) =>
        Assert.Equal(secret, ScriptSecrets.IsSecret(key, value));

    [Fact]
    public void Script_Output_Loses_Hidden_Values_And_Links()
    {
        var text = "Admin password: S3cr3t-pass\nImport this: hysteria2://pw@203.0.113.9:8443?sni=x#hy2\nSite: https://example.com/";
        var r = ScriptSecrets.Redact(text, ["S3cr3t-pass"], "(hidden)");
        Assert.Equal("Admin password: (hidden)\nImport this: (hidden)\nSite: https://example.com/", r);
    }

    [Fact]
    public async Task Script_Results_Hide_Credentials_When_The_User_Says_So()
    {
        using var f = new McpFixture(("vpn", McpAccess.Full));
        f.Vault.Update(d => d.Servers[0].Attributes =
        [
            new ServerAttribute { Key = "VLESS_URL", Value = "vless://6f1c3a52@203.0.113.9:443#vpn" },
            new ServerAttribute { Key = "FB_ADMIN_PASSWORD", Value = "S3cr3t-pass" },
            new ServerAttribute { Key = "SITE_URL", Value = "https://example.com/" },
        ]);
        var args = new JsonObject { ["server"] = "vpn" };

        var (error, text) = await f.Call("script_results", args);
        Assert.False(error);
        Assert.Contains("S3cr3t-pass", text); // full access: everything, as before
        Assert.Contains("vless://", text);

        f.Settings.HideScriptSecrets = true;
        (_, text) = await f.Call("script_results", (JsonObject)args.DeepClone());
        Assert.DoesNotContain("S3cr3t-pass", text);
        Assert.DoesNotContain("vless://", text);
        Assert.Contains("https://example.com/", text);
    }
}
