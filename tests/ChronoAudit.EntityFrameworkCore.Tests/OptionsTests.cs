using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>The options that run inside every save are checked when they are set, and the exclusion check is stable and cheap.</summary>
public sealed class OptionsTests
{
    [Theory]
    [InlineData("{0} (on behalf)")]
    [InlineData("{0}")]
    [InlineData("on behalf of {0}, delegated")]
    public void AValidFormat_IsUsedAsIs(string format)
    {
        var options = new AuditTrailOptions { DelegatedDisplayNameFormat = format };

        Assert.Equal(format, options.DelegatedDisplayNameFormat);
        Assert.Contains("Ada", string.Format(System.Globalization.CultureInfo.InvariantCulture, options.DelegatedDisplayNameFormat, "Ada"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no placeholder")]
    [InlineData("{0} (on behalf of {owner})")]
    [InlineData("{0} {1}")]
    [InlineData("{0} {")]
    public void ABadDelegatedFormat_IsRefusedWhenSet(string format)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AuditTrailOptions { DelegatedDisplayNameFormat = format });
    }

    [Fact]
    public void ABadFormat_LeavesThePreviousValueInPlace()
    {
        var options = new AuditTrailOptions();

        Assert.ThrowsAny<ArgumentException>(() => options.DelegatedDisplayNameFormat = "bad");

        Assert.Equal("{0} (on behalf)", options.DelegatedDisplayNameFormat);
    }

    [Fact]
    public void TheDefaultFormat_IsValid()
    {
        Assert.Equal("Ada (on behalf)", string.Format(System.Globalization.CultureInfo.InvariantCulture, AuditTrailOptions.Default.DelegatedDisplayNameFormat, "Ada"));
        Assert.Equal("System", AuditTrailOptions.Default.SystemUserId);
        Assert.Equal("System", AuditTrailOptions.Default.SystemDisplayName);
        Assert.Equal(1000, AuditTrailOptions.Default.BacklogWarnRows);
        Assert.Equal("ChronoAudit:Audit", AuditTrailOptions.Section);
    }

    [Fact]
    public void Exclusion_IsStable_ForAuditTableMarkedAndOrdinaryTypes()
    {
        var options = new DbContextOptionsBuilder<ShopDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ShopDb(
            options, new FakeAuditActor(), new FakeCorrelation(Guid.NewGuid()),
            new ScopedAuditLogManager(new FakeCorrelation(Guid.NewGuid())), new AuditSignalChannel(), AuditTrailOptions.Default);

        for (var round = 0; round < 2; round++)
        {
            Assert.True(db.IsAuditExcluded(typeof(AuditLogEntry)));
            Assert.True(db.IsAuditExcluded(typeof(Noise)));
            Assert.False(db.IsAuditExcluded(typeof(Order)));
            Assert.False(db.IsAuditExcluded(typeof(Receipt)));
        }
    }

    [Fact]
    public void TheModel_HasTheDocumentedAuditTable()
    {
        var options = new DbContextOptionsBuilder<ShopDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ShopDb(
            options, new FakeAuditActor(), new FakeCorrelation(Guid.NewGuid()),
            new ScopedAuditLogManager(new FakeCorrelation(Guid.NewGuid())), new AuditSignalChannel(), AuditTrailOptions.Default);

        var entity = db.Model.FindEntityType(typeof(AuditLogEntry))!;

        Assert.Equal("AuditLogEntries", entity.GetTableName());
        Assert.Equal(nameof(AuditLogEntry.SequenceNumber), Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
        var indexes = entity.GetIndexes().ToDictionary(i => i.GetDatabaseName()!);
        Assert.True(indexes["UX_AuditLogEntries_Id"].IsUnique);
        Assert.True(indexes["UX_AuditLogEntries_ChainIndex"].IsUnique);
        Assert.Equal(
            new[] { nameof(AuditLogEntry.CalendarDate), nameof(AuditLogEntry.SequenceNumber) },
            indexes["IX_AuditLogEntries_CalendarDate"].Properties.Select(p => p.Name));
        foreach (var name in new[] { "IX_AuditLogEntries_Actor", "IX_AuditLogEntries_Resource", "IX_AuditLogEntries_Occurred", "IX_AuditLogEntries_Unhashed" })
        {
            Assert.True(indexes.ContainsKey(name), name);
        }

        Assert.Equal(256, entity.FindProperty(nameof(AuditLogEntry.ResourceId))!.GetMaxLength());
        Assert.Equal(64, entity.FindProperty(nameof(AuditLogEntry.PayloadHash))!.GetMaxLength());
    }
}

/// <summary>Registration: one key, shared options, unique modules, and the configuration overload without the Binder package.</summary>
public sealed class RegistrationTests
{
    private static readonly byte[] Key = System.Text.Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    [Fact]
    public void TheFirstCall_RegistersTheServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditActor>(new FakeAuditActor());
        services.AddSingleton<ICorrelationContext>(new FakeCorrelation(Guid.NewGuid()));

        services.AddChronoAudit(Key);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(provider.GetRequiredService<AuditSignalChannel>(), provider.GetRequiredService<AuditSignalChannel>());
        Assert.Same(provider.GetRequiredService<ChronoAuditOptions>().Audit, provider.GetRequiredService<AuditTrailOptions>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<IAuditLogWriter>(), scope.ServiceProvider.GetRequiredService<IAuditLogAccessor>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Equal("Gregorian", provider.GetRequiredService<IChronoAuditCalendar>().Name);
    }

    [Fact]
    public void ASecondCall_WithADifferentKey_Throws_ButTheSameKeyExtendsTheRegistry()
    {
        var services = new ServiceCollection();
        var first = services.AddChronoAudit(Key);

        var second = services.AddChronoAudit((byte[])Key.Clone());
        Assert.Same(first.Registry, second.Registry);
        Assert.Throws<InvalidOperationException>(() => services.AddChronoAudit(System.Text.Encoding.UTF8.GetBytes("fedcba9876543210fedcba9876543210")));
    }

    [Fact]
    public void ASecondCall_WithADifferentOptionsInstance_Throws()
    {
        var services = new ServiceCollection();
        var options = new ChronoAuditOptions();
        services.AddChronoAudit(Key, options);

        services.AddChronoAudit(Key, options);
        services.AddChronoAudit(Key);
        Assert.Throws<InvalidOperationException>(() => services.AddChronoAudit(Key, new ChronoAuditOptions()));
    }

    [Fact]
    public void Modules_MustBeUnique_AndNamedWithSimpleCharacters()
    {
        var builder = new ServiceCollection().AddChronoAudit(Key);
        builder.AddModule<ShopDb>("shop");

        Assert.Throws<InvalidOperationException>(() => builder.AddModule<ShopDb>("other"));
        Assert.Throws<InvalidOperationException>(() => builder.AddModule<OtherDb>("SHOP"));
        foreach (var bad in new[] { "", " ", "has space", "slash/name", new string('a', 65) })
        {
            Assert.ThrowsAny<ArgumentException>(() => builder.AddModule<OtherDb>(bad));
        }

        builder.AddModule<OtherDb>("billing_2-x");
        Assert.Equal(2, builder.Registry.Modules.Count);
    }

    [Fact]
    public void TheConfigurationOverload_ReadsTheKeyAndTheAuditSection()
    {
        var values = new Dictionary<string, string?>
        {
            ["ChronoAudit:HmacKey"] = System.Text.Encoding.UTF8.GetString(Key),
            ["ChronoAudit:Audit:SystemUserId"] = "robot",
            ["ChronoAudit:Audit:SystemDisplayName"] = "Robot",
            ["ChronoAudit:Audit:DelegatedDisplayNameFormat"] = "{0} via proxy",
            ["ChronoAudit:Audit:BacklogWarnRows"] = "42",
        };
        var services = new ServiceCollection();

        var builder = services.AddChronoAudit(new FakeConfiguration(values), new FakeEnvironment("Production"));

        var audit = builder.Registry.Options.Audit;
        Assert.Equal("robot", audit.SystemUserId);
        Assert.Equal("Robot", audit.SystemDisplayName);
        Assert.Equal("{0} via proxy", audit.DelegatedDisplayNameFormat);
        Assert.Equal(42, audit.BacklogWarnRows);
        Assert.Equal(Key, builder.Registry.Key);
    }

    [Fact]
    public void TheConfigureCallback_RunsAfterTheSection()
    {
        var values = new Dictionary<string, string?>
        {
            ["ChronoAudit:HmacKey"] = System.Text.Encoding.UTF8.GetString(Key),
            ["ChronoAudit:Audit:SystemUserId"] = "robot",
        };

        var builder = new ServiceCollection().AddChronoAudit(
            new FakeConfiguration(values), new FakeEnvironment("Production"), o => o.Audit.SystemUserId = "override");

        Assert.Equal("override", builder.Registry.Options.Audit.SystemUserId);
    }

    [Fact]
    public void ABadValueInTheSection_StopsTheHostWithAnArgumentException()
    {
        var values = new Dictionary<string, string?>
        {
            ["ChronoAudit:HmacKey"] = System.Text.Encoding.UTF8.GetString(Key),
            ["ChronoAudit:Audit:DelegatedDisplayNameFormat"] = "no placeholder",
        };

        Assert.ThrowsAny<ArgumentException>(() =>
            new ServiceCollection().AddChronoAudit(new FakeConfiguration(values), new FakeEnvironment("Production")));
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    public void AMissingKey_IsAllowedOnlyInDevelopmentAndTesting(string environment, bool allowed)
    {
        var configuration = new FakeConfiguration([]);
        var services = new ServiceCollection();

        if (allowed)
        {
            var builder = services.AddChronoAudit(configuration, new FakeEnvironment(environment));
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(ChronoAuditKey.DevelopmentKey), builder.Registry.Key);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => services.AddChronoAudit(configuration, new FakeEnvironment(environment)));
        }
    }

    [Fact]
    public void ARefusedKey_FromTheOptions_IsRejectedOutsideDevelopment()
    {
        var keyText = System.Text.Encoding.UTF8.GetString(Key);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Key));
        var values = new Dictionary<string, string?> { ["ChronoAudit:HmacKey"] = keyText };

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddChronoAudit(
                new FakeConfiguration(values), new FakeEnvironment("Production"), o => o.RefusedKeySha256 = [hash]));
    }

    private sealed class OtherDb(
        DbContextOptions<OtherDb> options, IAuditActor actor, ICorrelationContext correlation, IAuditLogAccessor accessor, AuditSignalChannel channel)
        : AuditableDbContext(options, actor, correlation, accessor, channel);
}
