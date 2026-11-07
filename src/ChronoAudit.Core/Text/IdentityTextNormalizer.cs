namespace ChronoAudit;

/// <summary>The default normalizer: returns the text unchanged.</summary>
public sealed class IdentityTextNormalizer : ITextNormalizer
{
    /// <summary>The shared instance.</summary>
    public static IdentityTextNormalizer Instance { get; } = new();

    /// <inheritdoc />
    public string Normalize(string text) => text;
}
