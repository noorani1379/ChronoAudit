using System.Text;

namespace ChronoAudit.Core.Tests;

public class FieldChainHasherTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    [Fact]
    public void AValueThatContainsASeparator_CannotShiftTheBoundaryOfTheNextField()
    {
        using var hasher = new FieldChainHasher(Key);

        var first = hasher.Compute(null, ["a;b", "c"]);
        var second = hasher.Compute(null, ["a", "b;c"]);
        var third = hasher.Compute(null, ["a1;b", "c"]);
        var fourth = hasher.Compute(null, ["a", "1;bc"]);

        Assert.NotEqual(first, second);
        Assert.NotEqual(third, fourth);
    }

    [Fact]
    public void Null_DiffersFromEmpty()
    {
        using var hasher = new FieldChainHasher(Key);

        Assert.NotEqual(hasher.Compute(null, [null]), hasher.Compute(null, [string.Empty]));
        Assert.NotEqual(hasher.Compute(null, [null]), hasher.Compute("", [null]));
    }

    [Fact]
    public void ThePreviousHash_ChangesTheResult_AndTheHasherIsReusable()
    {
        using var hasher = new FieldChainHasher(Key);

        var a = hasher.Compute(null, ["x"]);
        var b = hasher.Compute(a, ["x"]);
        var aAgain = hasher.Compute(null, ["x"]);

        Assert.NotEqual(a, b);
        Assert.Equal(a, aAgain);
    }

    [Fact]
    public void ADifferentKey_GivesADifferentHash()
    {
        using var one = new FieldChainHasher(Key);
        using var two = new FieldChainHasher(Encoding.UTF8.GetBytes("fedcba9876543210fedcba9876543210"));

        Assert.NotEqual(one.Compute(null, ["x"]), two.Compute(null, ["x"]));
    }

    [Fact]
    public void GoldenHash_IsFrozen()
    {
        using var hasher = new FieldChainHasher(Key);

        var hash = hasher.Compute(null, ["a", null, ""]);

        // Changing this value means changing the byte layout of every stored hash: it breaks every chain ever written.
        // Computed independently: HMAC-SHA256 (key above) over the bytes "-1;1;a-1;0;", upper-case hex.
        Assert.Equal("4791CB2E1C3C1EC6AF3A6667DA1C9208B7E25C13B12F0F659CB574A349D86DC8", hash);
    }

    [Fact]
    public void NonAsciiText_IsHashedByItsUtf8Length()
    {
        using var hasher = new FieldChainHasher(Key);

        // U+06CC is two bytes in UTF-8, so the prefix is "2;".
        var hash = hasher.Compute(null, ["ی"]);

        var layout = Encoding.ASCII.GetBytes("-1;2;").Concat(Encoding.UTF8.GetBytes("ی")).ToArray();
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Key, layout)), hash);
    }

    [Fact]
    public void NullFields_Throw()
    {
        using var hasher = new FieldChainHasher(Key);

        Assert.Throws<ArgumentNullException>(() => hasher.Compute(null, null!));
    }
}
