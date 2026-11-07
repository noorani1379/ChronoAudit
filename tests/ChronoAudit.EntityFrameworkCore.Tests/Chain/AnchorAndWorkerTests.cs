using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronoaudit-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public sealed class AnchorTests
{
    private static ILogger<AuditAnchorLog> Logger(SqliteShop shop) => shop.Provider.GetRequiredService<ILogger<AuditAnchorLog>>();

    [Fact]
    public async Task AnAnchor_CatchesADeletedTail_AndAnEditedAnchorFile()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 5);
        var chain = shop.Provider.GetRequiredService<AuditChain>();
        await ChainTestHelper.HashAsync(shop, chain);
        var anchors = new AuditAnchorLog(chain.KeyRing, dir.Path, Logger(shop));

        // The chain alone cannot see a deleted tail; the anchor can.
        using (var scope = shop.Scope())
        {
            var head = (await chain.GetHeadAsync(SqliteShop.Get<ShopDb>(scope), CancellationToken.None))!.Value;
            Assert.Equal(5, head.ChainIndex);
            Assert.True(anchors.TryAppend("shop", head.ChainIndex, head.PayloadHash, DateTimeOffset.UtcNow));
        }

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            await db.AuditLogEntries.Where(a => a.ChainIndex >= 4).ExecuteDeleteAsync();
            Assert.True((await chain.VerifyAsync(db, CancellationToken.None)).IsValid);

            var read = anchors.ReadAll();
            Assert.Equal(0, read.TamperedLines);
            var check = await chain.VerifyAnchorsAsync(db, read.Anchors, CancellationToken.None);
            Assert.Equal(1, check.Checked);
            Assert.Equal(new long[] { 5 }, check.Mismatches);
        }

        // An edited line is found when the file is read.
        var file = Directory.EnumerateFiles(dir.Path).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"ChainIndex\":5", "\"ChainIndex\":4", StringComparison.Ordinal));
        var tampered = anchors.ReadAll();
        Assert.Equal(1, tampered.TamperedLines);
        Assert.Empty(tampered.Anchors);
    }

    [Fact]
    public async Task Verify_ReportsAMissingAnchoredRow()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 4);
        var chain = shop.Provider.GetRequiredService<AuditChain>();
        await ChainTestHelper.HashAsync(shop, chain);
        var anchors = new AuditAnchorLog(chain.KeyRing, dir.Path, Logger(shop));
        var source = shop.Provider.GetRequiredService<IAuditChainSources>().Find("shop")!;
        var head = (await source.GetHeadAsync(CancellationToken.None))!;
        anchors.TryAppend("shop", head.ChainIndex, head.PayloadHash, DateTimeOffset.UtcNow);

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 4).ExecuteDeleteAsync();
        }

        Assert.True((await source.VerifyAsync(CancellationToken.None)).IsValid);
        var check = await source.VerifyAnchorsAsync(anchors.ReadAll().Anchors.ToList(), CancellationToken.None);
        Assert.Equal(new long[] { 4 }, check.Mismatches);
    }

    [Fact]
    public async Task Anchors_AreCheckedOncePerRun()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        var counting = new CountingSource();
        var worker = new AuditAnchorWorker(
            new SingleSources(counting),
            new AuditAnchorLog(new AuditKeyRing(ChainTestHelper.KeyA), dir.Path, Logger(shop)),
            new AnchorOptions { Directory = dir.Path },
            TimeProvider.System,
            shop.Provider.GetRequiredService<ILogger<AuditAnchorWorker>>());

        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, counting.AnchorChecks);
        Assert.Equal(2, counting.RangeVerifications);
    }

    [Fact]
    public async Task AnAnchorFile_SurvivesAKeyRotation_AndAnUnknownKeyIsTampered()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        var logger = Logger(shop);
        new AuditAnchorLog(new AuditKeyRing(ChainTestHelper.KeyA), dir.Path, logger).TryAppend("shop", 1, "AA", DateTimeOffset.UtcNow);

        var rotated = new AuditAnchorLog(new AuditKeyRing(ChainTestHelper.KeyB, [ChainTestHelper.KeyA]), dir.Path, logger);
        rotated.TryAppend("shop", 2, "BB", DateTimeOffset.UtcNow);

        var read = rotated.ReadAll();
        Assert.Equal(0, read.TamperedLines);
        Assert.Equal(new long[] { 1, 2 }, read.Anchors.Select(a => a.ChainIndex));
        Assert.Equal(ChronoAuditKey.Fingerprint(ChainTestHelper.KeyA), read.Anchors[0].KeyFingerprint);

        var forgotten = new AuditAnchorLog(new AuditKeyRing(ChainTestHelper.KeyB), dir.Path, logger).ReadAll();
        Assert.Equal(1, forgotten.TamperedLines);
        Assert.Equal(new long[] { 2 }, forgotten.Anchors.Select(a => a.ChainIndex));
    }

    [Fact]
    public async Task AnAnchorWrite_ThatFails_IsLoggedAndReturnsFalse()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        Directory.CreateDirectory(dir.Path);
        var blocker = Path.Combine(dir.Path, "file");
        await File.WriteAllTextAsync(blocker, "x");
        var anchors = new AuditAnchorLog(new AuditKeyRing(ChainTestHelper.KeyA), blocker, Logger(shop));

        Assert.False(anchors.TryAppend("shop", 1, "AA", DateTimeOffset.UtcNow));
        Assert.Contains(shop.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be written", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 60, 10, "IntervalHours")]
    [InlineData(24, -1, 10, "StartupDelaySeconds")]
    [InlineData(24, 60, 0, "VerifyWindowRows")]
    public void BadAnchorOptions_AreRefusedAtStart(int interval, int delay, int window, string name)
    {
        var options = new AnchorOptions { IntervalHours = interval, StartupDelaySeconds = delay, VerifyWindowRows = window };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddChronoAudit(ChainTestHelper.KeyA, new ChronoAuditOptions { Anchors = { IntervalHours = 0 } }));
    }

    private sealed class CountingSource : IAuditChainSource
    {
        public int AnchorChecks { get; private set; }

        public int RangeVerifications { get; private set; }

        public string Module => "counting";

        public Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuditChainVerification> VerifyRangeAsync(long afterChainIndex, int maxRows, CancellationToken cancellationToken)
        {
            RangeVerifications++;
            return Task.FromResult(new AuditChainVerification(true, 1, 0, null, null, null));
        }

        public Task<AnchorCheck> VerifyAnchorsAsync(IReadOnlyCollection<AuditAnchor> anchors, CancellationToken cancellationToken)
        {
            AnchorChecks++;
            return Task.FromResult(AnchorCheck.None);
        }

        public Task<ChainHead?> GetHeadAsync(CancellationToken cancellationToken) => Task.FromResult<ChainHead?>(new ChainHead(1, 1, "AA"));
    }

    private sealed class SingleSources(IAuditChainSource source) : IAuditChainSources
    {
        public IReadOnlyList<string> Modules => [source.Module];

        public IAuditChainSource? Find(string module) => source;
    }
}

public sealed class AnchorWorkerTests
{
    private static AuditAnchorWorker Worker(SqliteShop shop, string directory)
        => new(
            shop.Provider.GetRequiredService<IAuditChainSources>(),
            new AuditAnchorLog(shop.Provider.GetRequiredService<AuditKeyRing>(), directory, shop.Provider.GetRequiredService<ILogger<AuditAnchorLog>>()),
            new AnchorOptions { Directory = directory, VerifyWindowRows = 100 },
            shop.Clock,
            shop.Provider.GetRequiredService<ILogger<AuditAnchorWorker>>());

    [Fact]
    public async Task OneRound_WritesAnAnchorOnlyForAChainThatHasHashedRows_AndLogsNothingCritical()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        var worker = Worker(shop, dir.Path);

        await worker.RunOnceAsync(CancellationToken.None);
        Assert.False(Directory.Exists(dir.Path));

        await ChainTestHelper.AddOrdersAsync(shop, 3);
        await ChainTestHelper.HashAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        await worker.RunOnceAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Directory.EnumerateFiles(dir.Path).Single()).Where(l => l.Length > 0).ToList();
        Assert.Single(lines);
        Assert.Contains("\"Module\":\"shop\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"ChainIndex\":3", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(shop.Logs.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task ADeletedTail_IsNoticedByTheNextRound_AsACriticalLog_WithoutRowValues()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 5);
        await ChainTestHelper.HashAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        var worker = Worker(shop, dir.Path);
        await worker.RunOnceAsync(CancellationToken.None);

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 5).ExecuteDeleteAsync();
        }

        shop.Clock.Advance(TimeSpan.FromDays(1));
        await worker.RunOnceAsync(CancellationToken.None);

        var critical = Assert.Single(shop.Logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("no longer match", critical.Message, StringComparison.Ordinal);
        Assert.Contains("shop", critical.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("order", critical.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Title", critical.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditedAnchorFile_IsALogCritical()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 2);
        await ChainTestHelper.HashAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        var worker = Worker(shop, dir.Path);
        await worker.RunOnceAsync(CancellationToken.None);

        var file = Directory.EnumerateFiles(dir.Path).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"Module\":\"shop\"", "\"Module\":\"shoq\"", StringComparison.Ordinal));
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Contains(shop.Logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("edited, cut or inserted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABrokenChain_IsALogCritical_WithTheFailureAndChainIndex()
    {
        using var dir = new TempDirectory();
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        await ChainTestHelper.HashAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 2)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Action, "forged"));
        }

        await Worker(shop, dir.Path).RunOnceAsync(CancellationToken.None);

        var critical = Assert.Single(shop.Logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("hash chain is broken", critical.Message, StringComparison.Ordinal);
        Assert.Contains("HashMismatch", critical.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", critical.Message, StringComparison.Ordinal);
    }
}

public sealed class HashWorkerTests
{
    private sealed class TestService(
        IServiceProvider provider, AuditChain chain, AuditSignalChannel signal, IReadOnlyList<(Type, string)> modules, AuditTrailOptions options, ILogger logger)
        : AuditHashBackgroundService(provider, chain, signal, modules, options, logger);

    private sealed class UnregisteredDb;

    [Fact]
    public async Task TheWorker_HashesWhatWasSaved_AndSleepsOnTheSignal()
    {
        await using var shop = await SqliteShop.CreateAsync(customize: b => b.AddHashWorkers(), fileBacked: true);
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        var worker = shop.Provider.GetServices<IHostedService>().OfType<AuditHashBackgroundService>().Single();

        // The shop is file-backed here (every context has its own connection), because a real background worker runs next to the test.
        // The test still waits on the log, not on the database, before it saves again.
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForLogAsync(shop, "Hashed 3 audit rows");

            // The worker is asleep on the signal; a later save wakes it up (the signal is raised after the commit).
            await ChainTestHelper.AddOrdersAsync(shop, 2);
            await WaitForLogAsync(shop, "Hashed 2 audit rows");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var verification = await ChainTestHelper.VerifyAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        Assert.True(verification.IsValid);
        Assert.Equal(5, verification.VerifiedCount);
        Assert.Equal(0, verification.PendingCount);
    }

    [Fact]
    public async Task AFailingModule_DoesNotBlockTheOthers()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 2);
        var service = new TestService(
            shop.Provider,
            shop.Provider.GetRequiredService<AuditChain>(),
            shop.Provider.GetRequiredService<AuditSignalChannel>(),
            [(typeof(UnregisteredDb), "broken"), (typeof(ShopDb), "shop")],
            AuditTrailOptions.Default,
            shop.Provider.GetRequiredService<ILogger<AuditHashBackgroundService>>());

        var succeeded = await service.DrainBacklogAsync(CancellationToken.None);

        Assert.False(succeeded);
        Assert.All(await shop.AuditRowsAsync(), r => Assert.NotNull(r.PayloadHash));
        Assert.Contains(shop.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("broken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABacklog_IsWarned()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        var options = new AuditTrailOptions { BacklogWarnRows = 2 };
        var service = new TestService(
            shop.Provider,
            shop.Provider.GetRequiredService<AuditChain>(),
            shop.Provider.GetRequiredService<AuditSignalChannel>(),
            [(typeof(ShopDb), "shop")],
            options,
            shop.Provider.GetRequiredService<ILogger<AuditHashBackgroundService>>());

        Assert.True(await service.DrainBacklogAsync(CancellationToken.None));

        var warning = Assert.Single(shop.Logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("3 unhashed rows", warning.Message, StringComparison.Ordinal);
        Assert.Contains("limit 2", warning.Message, StringComparison.Ordinal);

        shop.Logs.Entries.ToList().Clear();
        Assert.True(await service.DrainBacklogAsync(CancellationToken.None));
        Assert.Single(shop.Logs.Entries, e => e.Level == LogLevel.Warning);
    }


    private static async Task WaitForLogAsync(SqliteShop shop, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!shop.Logs.Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, "The log entry '" + text + "' did not appear in time.");
            await Task.Delay(25);
        }
    }
}

public sealed class ChainSourceTests
{
    [Fact]
    public async Task EachModule_IsVerifiedByItsOwnSource()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 4);
        await ChainTestHelper.HashAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        await ChainTestHelper.AddOrdersAsync(shop, 1);
        var sources = shop.Provider.GetRequiredService<IAuditChainSources>();

        Assert.Equal(new[] { "shop" }, sources.Modules);
        var source = sources.Find("SHOP");
        Assert.NotNull(source);
        var valid = await source.VerifyAsync(CancellationToken.None);
        Assert.True(valid.IsValid);
        Assert.Equal(4, valid.VerifiedCount);
        Assert.Equal(1, valid.PendingCount);
        var step = await source.VerifyRangeAsync(2, 1, CancellationToken.None);
        Assert.Equal(1, step.VerifiedCount);
        Assert.Equal(3, step.NextAfterChainIndex);

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 3)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResourceType, "Forged"));
        }

        var tampered = await source.VerifyAsync(CancellationToken.None);
        Assert.False(tampered.IsValid);
        Assert.Equal(3, tampered.FailedAtChainIndex);
    }

    [Fact]
    public async Task AnUnknownModule_IsAnError()
    {
        await using var shop = await SqliteShop.CreateAsync();

        Assert.Null(shop.Provider.GetRequiredService<IAuditChainSources>().Find("nope"));
        var rotation = await shop.Provider.GetRequiredService<IChainMaintenance>().RotateKeyAsync("nope", CancellationToken.None);
        Assert.Equal("Chain.UnknownModule", rotation.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.NotFound, rotation.Error.Kind);
    }

    [Fact]
    public async Task TheKeyFingerprint_IsLogged_NotTheKey()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var hosted = shop.Provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "ChronoAuditStartupLog");

        await hosted.StartAsync(CancellationToken.None);

        var entry = Assert.Single(shop.Logs.Entries, e => e.Message.StartsWith("ChronoAudit chain key fingerprint", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.EndsWith(ChronoAuditKey.Fingerprint(SqliteShop.Key), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(System.Text.Encoding.UTF8.GetString(SqliteShop.Key), entry.Message, StringComparison.Ordinal);
    }
}
