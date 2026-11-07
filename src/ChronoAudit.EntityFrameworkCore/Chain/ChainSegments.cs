using System.Security.Cryptography;
using System.Text.Json;

namespace ChronoAudit.EntityFrameworkCore;


internal sealed record ChainMarker(long Boundary, string PreviousFingerprint, string Fingerprint)
{
    public static readonly string[] Fields = ["PreviousKeyFingerprint", "KeyFingerprint", "BoundaryChainIndex"];

    public static bool TryParse(string? metadata, out ChainMarker marker)
    {
        marker = null!;
        if (string.IsNullOrEmpty(metadata))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;
            if (root.TryGetProperty(ChainMarker.Fields[0], out var previous) && root.TryGetProperty(ChainMarker.Fields[1], out var current)
                && root.TryGetProperty(ChainMarker.Fields[2], out var boundary) && boundary.TryGetInt64(out var index)
                && previous.GetString() is { } previousFp && current.GetString() is { } currentFp)
            {
                marker = new ChainMarker(index, previousFp, currentFp);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}

/// <summary>The key of each chain index: found from the rotation markers, with one HMAC instance per key used.</summary>
internal sealed class SegmentKeys(AuditKeyRing ring, List<ChainMarker> markers) : IDisposable
{
    private readonly Dictionary<string, IncrementalHash> _hmacs = new(StringComparer.Ordinal);

    public string FingerprintFor(long chainIndex)
    {
        if (markers.Count == 0)
        {
            return ring.CurrentFingerprint;
        }

        ChainMarker? applying = null;
        foreach (var marker in markers)
        {
            if (marker.Boundary < chainIndex)
            {
                applying = marker;
            }
        }

        return applying?.Fingerprint ?? markers[0].PreviousFingerprint;
    }

    public bool TryGetKey(long chainIndex, out byte[] key) => ring.TryGet(FingerprintFor(chainIndex), out key);

    public bool TryGetHmac(long chainIndex, out IncrementalHash hmac)
    {
        var fingerprint = FingerprintFor(chainIndex);
        if (_hmacs.TryGetValue(fingerprint, out var existing))
        {
            hmac = existing;
            return true;
        }

        if (!ring.TryGet(fingerprint, out var key))
        {
            hmac = null!;
            return false;
        }

        hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        _hmacs[fingerprint] = hmac;
        return true;
    }

    public void Dispose()
    {
        foreach (var hmac in _hmacs.Values)
        {
            hmac.Dispose();
        }
    }
}
