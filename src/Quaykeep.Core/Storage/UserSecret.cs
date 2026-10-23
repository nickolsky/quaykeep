using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Quaykeep.Core.Storage;

/// <summary>
/// A secret kept in settings.json encrypted for this user, so a copy of the file (a backup, a synced folder) is useless
/// to another account or on another PC. Windows: DPAPI, stored as "dpapi:&lt;base64&gt;". Linux / macOS: AES-GCM with a
/// random key in a file only this user can read, outside the data folder (backups never contain it), stored as
/// "aes:&lt;base64&gt;".
/// </summary>
public static class UserSecret
{
    private const string Prefix = "dpapi:";
    private const string AesPrefix = "aes:";
    private static readonly byte[] Entropy = "Quaykeep settings secret"u8.ToArray();

    /// <summary>The key file for Linux / macOS.</summary>
    internal static string KeyFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quaykeep", "user-secret.key");

    public static string Protect(string plain) => OperatingSystem.IsWindows()
        ? Prefix + Convert.ToBase64String(ProtectDpapi(Encoding.UTF8.GetBytes(plain)))
        : AesPrefix + Convert.ToBase64String(Seal(Encoding.UTF8.GetBytes(plain), Key(create: true)!));

    public static bool IsProtected(string? stored) =>
        stored?.StartsWith(Prefix, StringComparison.Ordinal) == true || stored?.StartsWith(AesPrefix, StringComparison.Ordinal) == true;

    /// <summary>The secret, or null when there is none or it cannot be decrypted (another user's or another PC's file).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!IsProtected(stored)) return stored; // written before secrets were encrypted
        try
        {
            if (stored.StartsWith(AesPrefix, StringComparison.Ordinal))
                return Key(create: false) is { } key ? Encoding.UTF8.GetString(Open(Convert.FromBase64String(stored[AesPrefix.Length..]), key)) : null;
            if (!OperatingSystem.IsWindows()) return null;
            return Encoding.UTF8.GetString(UnprotectDpapi(Convert.FromBase64String(stored[Prefix.Length..])));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectDpapi(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectDpapi(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    /// <summary>nonce (12) | tag (16) | ciphertext.</summary>
    private static byte[] Seal(byte[] plain, byte[] key)
    {
        var result = new byte[12 + 16 + plain.Length];
        var nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, result.AsSpan(28), result.AsSpan(12, 16), Entropy);
        return result;
    }

    private static byte[] Open(byte[] sealedData, byte[] key)
    {
        if (sealedData.Length < 28) throw new CryptographicException("too short");
        var plain = new byte[sealedData.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain, Entropy);
        return plain;
    }

    private static byte[]? Key(bool create)
    {
        if (File.Exists(KeyFile))
        {
            var existing = File.ReadAllBytes(KeyFile);
            if (existing.Length == 32) return existing;
        }
        if (!create) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        var key = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(KeyFile, options)) f.Write(key);
        return key;
    }
}
