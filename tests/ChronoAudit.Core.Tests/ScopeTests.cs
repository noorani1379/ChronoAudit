namespace ChronoAudit.Core.Tests;

public class ScopeTests
{
    [Fact]
    public void Own_SeesOnlyItsOwnItems()
    {
        var scope = ChronoAuditScope.OwnOf("u1");

        Assert.Equal(ScopeKind.Own, scope.Kind);
        Assert.True(scope.Sees("u1", "g1"));
        Assert.False(scope.Sees("u2", "g1"));
        Assert.False(scope.Sees(null, "g1"));
    }

    [Fact]
    public void Group_SeesItsGroupAndItsOwnItems()
    {
        var scope = ChronoAuditScope.GroupOf("u1", "g1");

        Assert.Equal(ScopeKind.Group, scope.Kind);
        Assert.True(scope.Sees("u2", "g1"));
        Assert.True(scope.Sees("u1", "other"));
        Assert.False(scope.Sees("u2", "other"));
        Assert.False(scope.Sees("u2", null));
    }

    [Fact]
    public void All_SeesEverything_EvenWithoutAUser()
    {
        var scope = ChronoAuditScope.Everything(null);

        Assert.Equal(ScopeKind.All, scope.Kind);
        Assert.True(scope.Sees("anyone", "any"));
        Assert.True(scope.Sees(null, null));
    }

    [Fact]
    public void OwnScopeWithoutAUser_SeesNothing()
    {
        var scope = new ChronoAuditScope(ScopeKind.Own, null, null);

        Assert.False(scope.Sees(null, null));
        Assert.False(scope.Sees("u1", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Factories_RejectMissingIds(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => ChronoAuditScope.OwnOf(id!));
        Assert.ThrowsAny<ArgumentException>(() => ChronoAuditScope.GroupOf(id!, "g1"));
        Assert.ThrowsAny<ArgumentException>(() => ChronoAuditScope.GroupOf("u1", id!));
    }
}
