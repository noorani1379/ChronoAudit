namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// The HMAC keys of an installation: the current key and the keys of earlier key segments. A chain is split into segments by rotation
/// markers; every row is verified with the key of its own segment, found here by fingerprint.
/// </summary>
public sealed class AuditKeyRing
{
    private readonly Dictionary<string, byte[]> _byFingerprint = new(StringComparer.Ordinal);

    /// <summary>Creates the ring.</summary>
    /// <param name="currentKey">The key new rows are hashed with.</param>
    /// <param name="previousKeys">Keys of earlier segments; a key equal to the current one is ignored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="currentKey"/> is null.</exception>
    public AuditKeyRing(byte[] currentKey, IEnumerable<byte[]>? previousKeys = null)
    {
        ArgumentNullException.ThrowIfNull(currentKey);
        Current = currentKey;
        CurrentFingerprint = ChronoAuditKey.Fingerprint(currentKey);
        _byFingerprint[CurrentFingerprint] = currentKey;
        foreach (var key in previousKeys ?? [])
        {
            _byFingerprint.TryAdd(ChronoAuditKey.Fingerprint(key), key);
        }
    }

    /// <summary>The key new rows are hashed with.</summary>
    public byte[] Current { get; }

    /// <summary>The fingerprint of <see cref="Current"/> (safe to log).</summary>
    public string CurrentFingerprint { get; }

    internal IEnumerable<string> Fingerprints => _byFingerprint.Keys;

    /// <summary>Finds the current or a previous key by its fingerprint.</summary>
    /// <param name="fingerprint">The fingerprint (see <see cref="ChronoAuditKey.Fingerprint(byte[])"/>).</param>
    /// <param name="key">The key, when found.</param>
    /// <returns>True when the ring has a key with this fingerprint.</returns>
    public bool TryGet(string fingerprint, out byte[] key)
    {
        if (fingerprint is not null && _byFingerprint.TryGetValue(fingerprint, out var found))
        {
            key = found;
            return true;
        }

        key = [];
        return false;
    }
}
