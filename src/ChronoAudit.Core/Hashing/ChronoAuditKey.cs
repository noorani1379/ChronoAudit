using System.Security.Cryptography;
using System.Text;

namespace ChronoAudit;

/// <summary>
/// Decides which HMAC key signs the hash chains. The chains are evidence, so the key is a secret of the installation, never a value in
/// the repository: outside development it must come from the environment of the process (<c>ChronoAudit__HmacKey</c>), be at least
/// <see cref="MinimumBytes"/> bytes, and not be a key that was ever published. Changing the key later makes every row hashed with the
/// old one fail verification, so keep a safe copy and use the key ring to rotate.
/// </summary>
public static class ChronoAuditKey
{
    /// <summary>The configuration key that holds the secret (the environment variable form is <c>ChronoAudit__HmacKey</c>).</summary>
    public const string ConfigurationKey = "ChronoAudit:HmacKey";

    /// <summary>The smallest key, in bytes, accepted outside development.</summary>
    public const int MinimumBytes = 32;

    /// <summary>Public on purpose: used only when development has no key of its own. Refused everywhere else.</summary>
    public const string DevelopmentKey = "chronoaudit-development-key-not-a-secret-000000000001";

    /// <summary>The key bytes, or an <see cref="InvalidOperationException"/> whose message never contains the key itself.</summary>
    /// <param name="configured">The value of <see cref="ConfigurationKey"/>.</param>
    /// <param name="allowDevelopmentKey">True only for development and test environments.</param>
    /// <param name="refusedKeySha256">Hex SHA-256 hashes of keys that must not be used outside development.</param>
    /// <returns>The UTF-8 bytes of the key.</returns>
    /// <exception cref="InvalidOperationException">The key is missing, too short or published, and development keys are not allowed.</exception>
    public static byte[] Resolve(string? configured, bool allowDevelopmentKey, IReadOnlyCollection<string>? refusedKeySha256 = null)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return allowDevelopmentKey
                ? Encoding.UTF8.GetBytes(DevelopmentKey)
                : throw new InvalidOperationException(
                    $"The ChronoAudit HMAC key is missing. Set the environment variable '{ConfigurationKey.Replace(":", "__", StringComparison.Ordinal)}' to a random secret of at least {MinimumBytes} bytes and keep a safe copy: without it the evidence chain cannot be verified.");
        }

        var bytes = Encoding.UTF8.GetBytes(configured);
        if (allowDevelopmentKey)
        {
            return bytes;
        }

        if (bytes.Length < MinimumBytes)
        {
            throw new InvalidOperationException($"The ChronoAudit HMAC key is too short: at least {MinimumBytes} bytes are required.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var refused = refusedKeySha256 ?? [];
        if (refused.Contains(hash, StringComparer.OrdinalIgnoreCase) || string.Equals(configured, DevelopmentKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ChronoAudit HMAC key is a published one (it was committed to a repository or is the development key). Generate a new random secret and set it in the environment.");
        }

        return bytes;
    }

    /// <summary>A short, non-secret identifier of a key, safe to log: the first 8 upper-case hex characters of its SHA-256.</summary>
    /// <param name="key">The key bytes.</param>
    /// <returns>Eight hex characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static string Fingerprint(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(SHA256.HashData(key))[..8];
    }
}
