using System.Security.Cryptography;

namespace SoulScreen.App;

/// <summary>
/// DPAPI, scoped to the current Windows user, for the one secret SoulScreen keeps on disk:
/// the PIN lock's PBKDF2 hash. Not a general-purpose secret store - the hash is already
/// useless without the PIN behind it - this only stops it being lifted wholesale and brute
/// forced offline on another machine or account.
/// </summary>
internal static class SecretProtection
{
    /// <summary>Encrypts <paramref name="plainBase64"/> (itself base64, e.g. a hash or a
    /// salt) to the current user, returning base64 ready to drop straight into settings.json.
    /// Null in, null out - callers do not need to special-case "nothing to protect".</summary>
    public static string? Protect(string? plainBase64)
    {
        if (plainBase64 is null) return null;
        var bytes = Convert.FromBase64String(plainBase64);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>Reverses <see cref="Protect"/>. Null when the value is missing, empty, or
    /// was protected under a different Windows user or a different machine's DPAPI master
    /// key - a settings file copied elsewhere must fail closed, not throw.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            var plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
