namespace ChronoAudit.Core.Tests;

public class PersonalDataMaskerTests
{
    [Theory]
    [InlineData("0012345678", "0012****78")]
    [InlineData("09123456789", "0912*****89")]
    [InlineData("1234567", "1234*67")]
    [InlineData("123456", "******")]
    [InlineData("12", "**")]
    [InlineData("  0012345678  ", "0012****78")]
    public void MaskNumber_KeepsTheFirstFourAndLastTwo_AndHidesShortValuesCompletely(string input, string expected)
    {
        Assert.Equal(expected, PersonalDataMasker.MaskNumber(input));
    }

    [Fact]
    public void MaskNumber_NormalizesPersianAndArabicIndicDigits_ToAscii()
    {
        // 0012345678 written with Persian digits, then with Arabic-Indic digits.
        var persian = "۰۰۱۲۳۴۵۶۷۸";
        var arabic = "٠٠١٢٣٤٥٦٧٨";

        Assert.Equal("0012****78", PersonalDataMasker.MaskNumber(persian));
        Assert.Equal("0012****78", PersonalDataMasker.MaskNumber(arabic));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskNumber_ReturnsNullAndBlankAsTheyAre(string? input)
    {
        Assert.Equal(input, PersonalDataMasker.MaskNumber(input));
    }

    [Fact]
    public void TheTextMask_IsThreeStars()
    {
        Assert.Equal("***", PersonalDataMasker.TextMask);
    }
}
