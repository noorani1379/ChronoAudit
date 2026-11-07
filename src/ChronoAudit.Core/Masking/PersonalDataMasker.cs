namespace ChronoAudit;

/// <summary>
/// Level 2 masking: personal data (ids, phone numbers, names) is stored whole in the audit rows, because it is evidence, and hidden when
/// it is shown, unless the caller was allowed to reveal it. Pure, no I/O. A number is normalized to ASCII digits first, so Persian and
/// Arabic-Indic digits are masked the same way.
/// </summary>
public static class PersonalDataMasker
{
    /// <summary>The mask of a text field (a name, an address, an email).</summary>
    public const string TextMask = "***";

    private const int KeepFirst = 4;
    private const int KeepLast = 2;

    /// <summary>
    /// A national id or a phone number: the first four and last two characters stay, the middle becomes stars (<c>0012****78</c>); a value
    /// too short to hide anything safely becomes all stars.
    /// </summary>
    /// <param name="value">The number; null or blank is returned as it is.</param>
    /// <returns>The masked number.</returns>
    public static string? MaskNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var text = PersianTextNormalizer.ToAsciiDigits(value.Trim())!;
        if (text.Length <= KeepFirst + KeepLast)
        {
            return new string('*', text.Length);
        }

        return string.Concat(text.AsSpan(0, KeepFirst), new string('*', text.Length - KeepFirst - KeepLast), text.AsSpan(text.Length - KeepLast));
    }
}
