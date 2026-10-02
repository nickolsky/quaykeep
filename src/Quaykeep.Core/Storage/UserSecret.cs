using System.Security.Cryptography;
using System.Text;

namespace Quaykeep.Core.Storage;

/// <summary>
/// A secret kept in settings.json encrypted for this Windows user (DPAPI): a copy of the file is useless to another
/// account or on another PC. Stored as "dpapi:&lt;base64&gt;".
/// </summary>
public static class UserSecret
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "Quaykeep settings secret"u8.ToArray();

    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static bool IsProtected(string? stored) => stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>The secret, or null when there is none or it cannot be decrypted (another user's or another PC's file).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!IsProtected(stored)) return stored; // written before secrets were encrypted
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
