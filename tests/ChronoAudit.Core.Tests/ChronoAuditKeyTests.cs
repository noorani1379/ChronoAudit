using System.Security.Cryptography;
using System.Text;

namespace ChronoAudit.Core.Tests;

public class ChronoAuditKeyTests
{
    private const string GoodKey = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingKey_OutsideDevelopment_Throws_AndNamesTheEnvironmentVariable(string? configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ChronoAuditKey.Resolve(configured, allowDevelopmentKey: false));

        Assert.Contains("ChronoAudit__HmacKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortKey_OutsideDevelopment_Throws_WithoutRevealingTheKey()
    {
        const string shortKey = "short-secret-value";

        var ex = Assert.Throws<InvalidOperationException>(() => ChronoAuditKey.Resolve(shortKey, allowDevelopmentKey: false));

        Assert.DoesNotContain(shortKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedKey_OutsideDevelopment_Throws_CaseInsensitively()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GoodKey)));

        Assert.Throws<InvalidOperationException>(() => ChronoAuditKey.Resolve(GoodKey, false, [hash]));
        Assert.Throws<InvalidOperationException>(() => ChronoAuditKey.Resolve(GoodKey, false, [hash.ToLowerInvariant()]));
    }

    [Fact]
    public void TheDevelopmentKey_IsRefused_OutsideDevelopment()
    {
        Assert.True(Encoding.UTF8.GetByteCount(ChronoAuditKey.DevelopmentKey) >= ChronoAuditKey.MinimumBytes);
        Assert.Throws<InvalidOperationException>(() => ChronoAuditKey.Resolve(ChronoAuditKey.DevelopmentKey, allowDevelopmentKey: false));
    }

    [Fact]
    public void AGoodKey_OutsideDevelopment_IsReturnedAsUtf8()
    {
        var bytes = ChronoAuditKey.Resolve(GoodKey, allowDevelopmentKey: false, refusedKeySha256: ["not-this-one"]);

        Assert.Equal(Encoding.UTF8.GetBytes(GoodKey), bytes);
    }

    [Fact]
    public void Development_FallsBackToThePublicKey_WhenNothingIsConfigured()
    {
        var bytes = ChronoAuditKey.Resolve(null, allowDevelopmentKey: true);

        Assert.Equal(Encoding.UTF8.GetBytes(ChronoAuditKey.DevelopmentKey), bytes);
    }

    [Fact]
    public void Development_AcceptsAnyNonEmptyKeyAsIs()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("tiny"), ChronoAuditKey.Resolve("tiny", allowDevelopmentKey: true));
    }

    [Fact]
    public void Fingerprint_HasEightUpperCaseHexCharacters_AndIsStable()
    {
        var key = Encoding.UTF8.GetBytes(GoodKey);

        var fingerprint = ChronoAuditKey.Fingerprint(key);

        Assert.Equal(8, fingerprint.Length);
        Assert.Matches("^[0-9A-F]{8}$", fingerprint);
        Assert.Equal(fingerprint, ChronoAuditKey.Fingerprint(key));
        Assert.NotEqual(fingerprint, ChronoAuditKey.Fingerprint(Encoding.UTF8.GetBytes(GoodKey + "x")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(key))[..8], fingerprint);
    }
}
