using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChronoAudit;

/// <summary>
/// HMAC-SHA256 over an ordered list of text fields and the previous hash. Not thread-safe; one per batch.
/// Every field is length-prefixed (<c>-1;</c> for null), so a separator inside a value can never shift the boundary of the next field.
/// </summary>
/// <remarks>The byte layout is frozen: every stored hash depends on it. A golden test pins it.</remarks>
/// <param name="key">The HMAC key.</param>
public sealed class FieldChainHasher(byte[] key) : IDisposable
{
    private readonly IncrementalHash _hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);

    /// <summary>The hash of the fields in order, chained to <paramref name="previousHash"/> (null for the first).</summary>
    /// <param name="previousHash">The hash of the previous row, or null for the first row.</param>
    /// <param name="fields">The fields in their fixed order.</param>
    /// <returns>The upper-case hex hash.</returns>
    public string Compute(string? previousHash, IReadOnlyList<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        Append(previousHash);
        foreach (var field in fields)
        {
            Append(field);
        }

        Span<byte> hash = stackalloc byte[32];
        _hmac.TryGetHashAndReset(hash, out _);
        return Convert.ToHexString(hash);
    }

    private void Append(string? text)
    {
        if (text is null)
        {
            _hmac.AppendData("-1;"u8);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        _hmac.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ";"));
        _hmac.AppendData(bytes);
    }

    /// <inheritdoc />
    public void Dispose() => _hmac.Dispose();
}
