using System.Diagnostics.CodeAnalysis;

namespace ChronoAudit;

/// <summary>
/// Canonical form for Persian text at the input boundary, so equality, search and unique indexes agree.
/// Arabic yeh and kaf become Persian yeh and kaf, and Persian and Arabic-Indic digits become ASCII digits.
/// ZWNJ (U+200C) and every other character are left untouched. Allocation-free when nothing changes.
/// </summary>
/// <remarks>Every non-ASCII character of this source is a <c>\uXXXX</c> escape on purpose (the source hygiene test forbids literal letters).</remarks>
public sealed class PersianTextNormalizer : ITextNormalizer
{
    private const char ArabicYeh = '\u064A';
    private const char ArabicAlefMaksura = '\u0649';
    private const char PersianYeh = '\u06CC';
    private const char ArabicKaf = '\u0643';
    private const char PersianKaf = '\u06A9';
    private const char PersianZero = '\u06F0';
    private const char PersianNine = '\u06F9';
    private const char ArabicIndicZero = '\u0660';
    private const char ArabicIndicNine = '\u0669';

    /// <summary>The shared instance.</summary>
    public static PersianTextNormalizer Instance { get; } = new();

    /// <summary>Letters and digits.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    public string Normalize(string text) => Map(text, letters: true, digits: true);

    /// <summary>Letters only (Arabic yeh, alef maksura and kaf become Persian yeh and kaf). Digits are kept as typed.</summary>
    /// <param name="value">The text, or null.</param>
    /// <returns>The normalized text, or null when <paramref name="value"/> is null.</returns>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? NormalizeLetters(string? value) => Map(value, letters: true, digits: false);

    /// <summary>Digits only (Persian and Arabic-Indic digits become 0-9). Use it for national ids, mobile numbers and codes.</summary>
    /// <param name="value">The text, or null.</param>
    /// <returns>The text with ASCII digits, or null when <paramref name="value"/> is null.</returns>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? ToAsciiDigits(string? value) => Map(value, letters: false, digits: true);

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Map(string? value, bool letters, bool digits)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (IndexOfFirstChange(value, letters, digits) < 0)
        {
            return value;
        }

        return string.Create(value.Length, (value, letters, digits), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = MapChar(state.value[i], state.letters, state.digits);
            }
        });
    }

    private static int IndexOfFirstChange(string value, bool letters, bool digits)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (MapChar(value[i], letters, digits) != value[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static char MapChar(char c, bool letters, bool digits)
    {
        if (letters)
        {
            switch (c)
            {
                case ArabicYeh:
                case ArabicAlefMaksura:
                    return PersianYeh;
                case ArabicKaf:
                    return PersianKaf;
            }
        }

        if (digits)
        {
            if (c is >= PersianZero and <= PersianNine)
            {
                return (char)('0' + (c - PersianZero));
            }

            if (c is >= ArabicIndicZero and <= ArabicIndicNine)
            {
                return (char)('0' + (c - ArabicIndicZero));
            }
        }

        return c;
    }
}
