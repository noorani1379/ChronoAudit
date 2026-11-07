namespace ChronoAudit;

/// <summary>
/// Brings text to one canonical form at the input boundary, so equality, search and unique indexes agree.
/// The analytics text filters normalize their input once with the configured normalizer.
/// </summary>
public interface ITextNormalizer
{
    /// <summary>Returns the canonical form of <paramref name="text"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    string Normalize(string text);
}
