using System.Security.Cryptography;
using System.Text;

namespace Knovara.ControlPlane.Services;

/// <summary>
/// Enrollment codes are what an admin hands to a person to install the desktop app, so they are typed by
/// hand: an unambiguous alphabet (no 0/O, 1/I/L), grouped for reading, ~59 bits of entropy, and stored only as
/// a hash so a database leak does not leak usable codes.
/// </summary>
public static class EnrollmentCodes
{
    public const string Prefix = "KNV";
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // 31 symbols
    private const int Length = 12;

    /// <summary>A new random code such as <c>KNV-7QH2-M9XD-4TRK</c>.</summary>
    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{Prefix}-{chars[..4]}-{chars[4..8]}-{chars[8..]}";
    }

    /// <summary>Upper-cases and strips spaces/hyphens/the prefix, so pasted or retyped codes still match. Null if it cannot be a code.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var compact = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.StartsWith(Prefix, StringComparison.Ordinal)) compact = compact[Prefix.Length..];
        return compact.Length == Length && compact.All(c => Alphabet.Contains(c)) ? compact : null;
    }

    public static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
}

/// <summary>The long-lived credential a device keeps after enrolling. 256 random bits, so a plain SHA-256 hash is sufficient to store.</summary>
public static class DeviceSecrets
{
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>Constant-time comparison, so response timing reveals nothing about a stored hash.</summary>
    public static bool Verify(string secret, string storedHash)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(storedHash));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
