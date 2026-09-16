using System.Security.Cryptography;
using System.Text;

namespace SoulScreen.App.Logic;

/// <summary>
/// Hashing and checking the PIN that guards SoulScreen from a screen unlocked by someone
/// else in the room.
/// <para>
/// The PIN itself is never stored - only a salted PBKDF2 hash of it, the same idea a login
/// system uses for a password. A PIN forgotten cannot be recovered, only replaced by turning
/// the lock off and on again from an unlocked session, or by clearing <c>settings.json</c>'s
/// <c>Lock</c> section by hand - the same "physical access already means access" trade every
/// local screen lock makes.
/// </para>
/// Deliberately free of WPF so the hashing and comparison can be tested on their own.
/// </summary>
public static class AppLock
{
    public const int MinPinLength = 4;
    public const int MaxPinLength = 8;

    /// <summary>PBKDF2 rounds for a new PIN. Values already on disk keep whatever they were
    /// hashed with, so raising this later never invalidates a PIN set under the old count.</summary>
    public const int DefaultIterations = 200_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>True when <paramref name="pin"/> is the shape a PIN is allowed to be: digits
    /// only, four to eight of them. Letters are refused - a PIN, not a password, is the point,
    /// so the unlock screen can be a numeric pad.</summary>
    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= MinPinLength and <= MaxPinLength } && pin.All(char.IsAsciiDigit);

    /// <summary>Salts and hashes a new PIN. The salt and hash are both returned as base64,
    /// which is what <see cref="AppSettings.LockSettings"/> persists.</summary>
    public static (string SaltBase64, string HashBase64) Hash(string pin, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>
    /// True when <paramref name="pin"/> hashes to <paramref name="expectedHashBase64"/> under
    /// <paramref name="saltBase64"/> and <paramref name="iterations"/>. Compared in constant
    /// time, as any secret comparison should be; a settings file that has been hand-edited
    /// into something that is not valid base64 fails closed rather than throwing.
    /// </summary>
    public static bool Verify(string pin, string saltBase64, string expectedHashBase64, int iterations)
    {
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(saltBase64);
            expected = Convert.FromBase64String(expectedHashBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (iterations <= 0) iterations = DefaultIterations;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
