namespace ChronoAudit.Core.Tests;

public class TextNormalizerTests
{
    // Escapes keep the test bytes independent of any editor or console code page.
    private const string ArabicYeh = "ي";
    private const string AlefMaksura = "ى";
    private const string PersianYeh = "ی";
    private const string ArabicKaf = "ك";
    private const string PersianKaf = "ک";
    private const string Zwnj = "‌";

    [Fact]
    public void ArabicYeh_BecomesPersianYeh()
    {
        Assert.Equal(PersianYeh, PersianTextNormalizer.Instance.Normalize(ArabicYeh));
    }

    [Fact]
    public void AlefMaksura_BecomesPersianYeh()
    {
        Assert.Equal(PersianYeh, PersianTextNormalizer.Instance.Normalize(AlefMaksura));
    }

    [Fact]
    public void ArabicKaf_BecomesPersianKaf()
    {
        Assert.Equal(PersianKaf, PersianTextNormalizer.Instance.Normalize(ArabicKaf));
    }

    [Fact]
    public void PersianDigits_BecomeAscii()
    {
        Assert.Equal("0123456789", PersianTextNormalizer.Instance.Normalize("۰۱۲۳۴۵۶۷۸۹"));
    }

    [Fact]
    public void ArabicIndicDigits_BecomeAscii()
    {
        Assert.Equal("0123456789", PersianTextNormalizer.Instance.Normalize("٠١٢٣٤٥٦٧٨٩"));
    }

    [Fact]
    public void Zwnj_IsKept_AndNeighboursAreMapped()
    {
        var input = "می" + Zwnj + "كند";

        var output = PersianTextNormalizer.Instance.Normalize(input);

        Assert.Equal("می" + Zwnj + "کند", output);
        Assert.Contains(Zwnj, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AsciiAndOtherText_IsUnchanged_AndTheSameInstanceIsReturned()
    {
        var input = "Order 42 ما";

        Assert.Same(input, PersianTextNormalizer.Instance.Normalize(input));
    }

    [Fact]
    public void NormalizeLetters_KeepsDigits_AndToAsciiDigits_KeepsLetters()
    {
        var mixed = ArabicYeh + "۱" + ArabicKaf + "٢";

        Assert.Equal(PersianYeh + "۱" + PersianKaf + "٢", PersianTextNormalizer.NormalizeLetters(mixed));
        Assert.Equal(ArabicYeh + "1" + ArabicKaf + "2", PersianTextNormalizer.ToAsciiDigits(mixed));
    }

    [Fact]
    public void NullAndEmpty_PassThrough()
    {
        Assert.Null(PersianTextNormalizer.ToAsciiDigits(null));
        Assert.Null(PersianTextNormalizer.NormalizeLetters(null));
        Assert.Equal(string.Empty, PersianTextNormalizer.ToAsciiDigits(string.Empty));
    }

    [Fact]
    public void Identity_ReturnsTheSameInstance()
    {
        var input = ArabicYeh + "۱";

        Assert.Same(input, IdentityTextNormalizer.Instance.Normalize(input));
    }
}
