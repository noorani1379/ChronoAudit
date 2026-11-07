using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class ActivityChainTests
{
    private static StoreChain<ApiRequestLog> Chain(ActivityHost host) => host.Provider.GetRequiredService<StoreChain<ApiRequestLog>>();

    private static async Task<AuditChainVerification> VerifyAsync(ActivityHost host, long after = 0, int maxRows = int.MaxValue, int batch = 1000)
    {
        using var scope = host.Provider.CreateScope();
        return await Chain(host).VerifyAsync(scope.ServiceProvider.GetRequiredService<StoreDb>(), CancellationToken.None, batch, after, maxRows);
    }

    [Fact]
    public async Task TheChain_IsBuilt_AndVerifiesFromTheStart()
    {
        await using var host = await ActivityHost.CreateAsync();
        for (var i = 1; i <= 5; i++)
        {
            await host.LogAsync(i, body: i % 2 == 0 ? "{\"n\":" + i + "}" : null);
        }

        Assert.Equal(5, await host.HashAsync());
        Assert.Equal(0, await host.HashAsync());

        var verification = await VerifyAsync(host);
        Assert.True(verification.IsValid);
        Assert.Equal(5, verification.VerifiedCount);
        var rows = await host.ReadStoreAsync(db => db.Set<ApiRequestLog>().AsNoTracking().OrderBy(r => r.Id).ToListAsync());
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, rows.Select(r => r.ChainIndex!.Value));
        Assert.Null(rows[0].PreviousHash);
        Assert.Equal(rows[0].PayloadHash, rows[1].PreviousHash);
    }

    [Fact]
    public async Task AnEditedColumn_IsAHashMismatch()
    {
        await using var host = await ActivityHost.CreateAsync();
        for (var i = 1; i <= 3; i++)
        {
            await host.LogAsync(i);
        }

        await host.HashAsync();
        await host.ReadStoreAsync(db => db.Set<ApiRequestLog>().Where(r => r.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(r => r.StatusCode, (short)500)));

        var verification = await VerifyAsync(host);

        Assert.False(verification.IsValid);
        Assert.Equal(AuditChainFailure.HashMismatch, verification.Failure);
        Assert.Equal(2, verification.FailedAtChainIndex);
        Assert.Equal(2, verification.FailedAtSequence);
    }

    [Fact]
    public async Task AnEditedBody_IsAHashMismatch_BecauseTheBodyIsPartOfTheHash()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.LogAsync(1, body: "{\"a\":1}");
        await host.LogAsync(2);
        await host.HashAsync();
        await host.ReadStoreAsync(db => db.Set<ApiRequestBody>().ExecuteUpdateAsync(s => s.SetProperty(b => b.Body, "{\"a\":2}")));

        var verification = await VerifyAsync(host);

        Assert.Equal(AuditChainFailure.HashMismatch, verification.Failure);
        Assert.Equal(1, verification.FailedAtChainIndex);
    }

    [Fact]
    public async Task ADeletedRow_IsABrokenLink()
    {
        await using var host = await ActivityHost.CreateAsync();
        for (var i = 1; i <= 4; i++)
        {
            await host.LogAsync(i);
        }

        await host.HashAsync();
        await host.ReadStoreAsync(db => db.Set<ApiRequestLog>().Where(r => r.Id == 2).ExecuteDeleteAsync());

        var verification = await VerifyAsync(host);

        Assert.Equal(AuditChainFailure.BrokenLink, verification.Failure);
        Assert.Equal(2, verification.FailedAtChainIndex);
        Assert.Null(verification.FailedAtSequence);
    }

    [Fact]
    public async Task UnhashedRows_ArePending_NotBroken_AndStepsCoverEverything()
    {
        await using var host = await ActivityHost.CreateAsync();
        for (var i = 1; i <= 6; i++)
        {
            await host.LogAsync(i);
        }

        await host.HashAsync();
        await host.LogAsync(7);

        long verified = 0;
        long? next = 0;
        while (next is not null)
        {
            var step = await VerifyAsync(host, next.Value, maxRows: 2, batch: 1);
            Assert.True(step.IsValid);
            verified += step.VerifiedCount;
            next = step.NextAfterChainIndex;
        }

        Assert.Equal(6, verified);
        var all = await VerifyAsync(host);
        Assert.Equal(1, all.PendingCount);
    }

    [Fact]
    public async Task AValueThatContainsASeparator_CannotShiftTheBoundaryOfTheNextField()
    {
        using var hasher = new FieldChainHasher(SqliteShop.Key);

        var first = hasher.Compute(null, ["POST", "/a;b", "c"]);
        var second = hasher.Compute(null, ["POST", "/a", "b;c"]);

        Assert.NotEqual(first, second);
        await Task.CompletedTask;
    }

    [Fact]
    public void TheActivityLayout_IsPinned()
    {
        // Computed independently: HMAC-SHA256 (key 0123456789abcdef0123456789abcdef) over each field written as "length;text" ("-1;" for null).
        var log = new ApiRequestLog
        {
            Id = 1,
            RequestId = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
            OccurredAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DurationMs = 12,
            UserId = "u1",
            SessionId = "s1",
            CalendarYear = 2026,
            CalendarMonth = 1,
            CalendarDay = 1,
            CalendarQuarter = 1,
            HttpMethod = "POST",
            Path = "/api/x",
            EndpointKey = "/api/x",
            QueryString = "?a=1",
            StatusCode = 200,
            IpAddress = "10.0.0.1",
            UserAgent = "ua",
            HasBody = true,
            Body = new ApiRequestBody { ContentType = "application/json", Body = "{\"a\":1}" },
        };
        using var hasher = new FieldChainHasher(ChainTestHelper.KeyA);

        Assert.Equal("B60A5E8D13E0C5BF4003CD360FE9107F5C06CC3ABBC96D8A0843EAC956C28F09", hasher.Compute(null, StoreLayouts.Fields(log)));
    }

    [Fact]
    public async Task AKeyChangeWithoutRotation_StopsHashing_AndLogsCritical()
    {
        await using var first = await ActivityHost.CreateAsync(key: ChainTestHelper.KeyA);
        await first.LogAsync(1);
        await first.LogAsync(2);
        await first.HashAsync();

        // Same databases, but the process now runs with key B and no rotation: nothing may be hashed with it.
        var rows = await first.ReadStoreAsync(db => db.Set<ApiRequestLog>().AsNoTracking().ToListAsync());
        Assert.All(rows, r => Assert.NotNull(r.PayloadHash));
        var chainB = new StoreChain<ApiRequestLog>(
            new AuditKeyRing(ChainTestHelper.KeyB),
            new StoreMarkerReader(first.Provider.GetRequiredService<ChronoAuditRegistry>(), first.Provider.GetRequiredService<IServiceScopeFactory>()),
            StoreChainModules.Activity,
            q => q.Include(x => x.Body),
            StoreLayouts.Fields,
            (row, index, previous, hash) => row.SetHash(index, previous, hash),
            first.Provider.GetRequiredService<ILogger<StoreChain<ApiRequestLog>>>());
        await first.LogAsync(3);

        int result;
        using (var scope = first.Provider.CreateScope())
        {
            result = await chainB.HashAllPendingAsync(scope.ServiceProvider.GetRequiredService<StoreDb>(), 100, CancellationToken.None);
        }

        Assert.Equal(-1, result);
        Assert.Contains(first.Logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("hashed with another key", StringComparison.Ordinal));
        Assert.Equal(1, await first.ReadStoreAsync(db => db.Set<ApiRequestLog>().CountAsync(r => r.PayloadHash == null)));
    }

    [Fact]
    public async Task AfterARotation_EachSegmentUsesItsOwnKey_ForTheActivityAndTheIntentChain()
    {
        // Rows hashed with key A, then the host runs with key B and A as a previous key.
        await using var host = await ActivityHost.CreateAsync(key: ChainTestHelper.KeyB, configure: o => o.PreviousHmacKeys.Add(ChainTestHelper.KeyA));
        await host.LogAsync(1);
        await host.LogAsync(2);
        await host.IntentAsync(1);
        var oldKeyChain = new StoreChain<ApiRequestLog>(
            new AuditKeyRing(ChainTestHelper.KeyA),
            new StoreMarkerReader(host.Provider.GetRequiredService<ChronoAuditRegistry>(), host.Provider.GetRequiredService<IServiceScopeFactory>()),
            StoreChainModules.Activity, q => q.Include(x => x.Body), StoreLayouts.Fields, (row, index, previous, hash) => row.SetHash(index, previous, hash),
            host.Provider.GetRequiredService<ILogger<StoreChain<ApiRequestLog>>>());
        using (var scope = host.Provider.CreateScope())
        {
            Assert.Equal(2, await oldKeyChain.HashAllPendingAsync(scope.ServiceProvider.GetRequiredService<StoreDb>(), 100, CancellationToken.None));
        }

        var maintenance = host.Provider.GetRequiredService<IChainMaintenance>();
        var rotated = await maintenance.RotateKeyAsync("chronoaudit-activity", CancellationToken.None);
        Assert.True(rotated.IsSuccess, rotated.IsFailure ? rotated.Error.Message : null);
        Assert.Equal("Chain.RotationPending", (await maintenance.RotateKeyAsync("chronoaudit-activity", CancellationToken.None)).Error.Code);

        // The marker is a row of the audit module; the audit chain hashes it too, with key B, and ignores it for its own segments.
        await host.LogAsync(3);
        Assert.Equal(1, await host.HashAsync() - 1);

        var verification = await VerifyAsync(host);
        Assert.True(verification.IsValid);
        Assert.Equal(3, verification.VerifiedCount);
        var marker = Assert.Single(await host.AuditRowsAsync(), r => r.Action == AuditActions.KeyRotated);
        Assert.Equal("chronoaudit-activity", marker.ResourceId);
        Assert.Contains("\"BoundaryChainIndex\":2", marker.Metadata, StringComparison.Ordinal);

        // A ring without key A cannot verify the first segment.
        using var ringScope = host.Provider.CreateScope();
        var withoutA = new StoreChain<ApiRequestLog>(
            new AuditKeyRing(ChainTestHelper.KeyB), host.Provider.GetRequiredService<StoreMarkerReader>(), StoreChainModules.Activity,
            q => q.Include(x => x.Body), StoreLayouts.Fields, (row, index, previous, hash) => row.SetHash(index, previous, hash),
            host.Provider.GetRequiredService<ILogger<StoreChain<ApiRequestLog>>>());
        Assert.Equal(AuditChainFailure.UnknownKey, (await withoutA.VerifyAsync(ringScope.ServiceProvider.GetRequiredService<StoreDb>(), CancellationToken.None)).Failure);

        // The intent chain was never hashed with A, so there is nothing to rotate there until it has a head.
        Assert.Equal("Chain.NothingToRotate", (await maintenance.RotateKeyAsync("chronoaudit-intents", CancellationToken.None)).Error.Code);
        Assert.Equal("Chain.UnknownModule", (await maintenance.RotateKeyAsync("chronoaudit-nope", CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task WithoutAnAuditedModule_TheStoreChainsCannotBeRotated()
    {
        await using var host = await ActivityHost.CreateAsync(withAuditedModule: false);

        var result = await host.Provider.GetRequiredService<IChainMaintenance>().RotateKeyAsync("chronoaudit-activity", CancellationToken.None);

        Assert.Equal("Chain.UnknownModule", result.Error.Code);
    }

    [Fact]
    public async Task TheStoreChains_AreChainSources_ForTheAnchorWorkerAndVerification()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.LogAsync(1);
        await host.IntentAsync(1);
        await host.HashAsync();
        var sources = host.Provider.GetRequiredService<IAuditChainSources>();

        Assert.Contains("chronoaudit-activity", sources.Modules);
        Assert.Contains("chronoaudit-intents", sources.Modules);
        var activity = sources.Find("chronoaudit-activity")!;
        Assert.True((await activity.VerifyAsync(CancellationToken.None)).IsValid);
        var head = (await activity.GetHeadAsync(CancellationToken.None))!;
        Assert.Equal(1, head.ChainIndex);
        Assert.True((await sources.Find("chronoaudit-intents")!.VerifyAsync(CancellationToken.None)).IsValid);

        using var dir = new TempDirectory();
        var anchors = new AuditAnchorLog(host.Provider.GetRequiredService<AuditKeyRing>(), dir.Path, host.Provider.GetRequiredService<ILogger<AuditAnchorLog>>());
        anchors.TryAppend("chronoaudit-activity", head.ChainIndex, head.PayloadHash, DateTimeOffset.UtcNow);
        await host.ReadStoreAsync(db => db.Set<ApiRequestLog>().Where(r => r.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(r => r.PayloadHash, "TAMPERED")));
        var check = await activity.VerifyAnchorsAsync(anchors.ReadAll().Anchors.ToList(), CancellationToken.None);
        Assert.Equal(new long[] { 1 }, check.Mismatches);
    }

    [Fact]
    public async Task TheHashWorkerRound_ReportsAFailureAsMinusOne()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.LogAsync(1);
        await host.ReadStoreAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE ApiMutationIntents"));

        Assert.Equal(-1, await host.HashAsync());
        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed to hash", StringComparison.Ordinal));
    }
}

public sealed class IntentChainTests
{
    [Fact]
    public async Task TheIntentChain_IsBuilt_AndAnEditedRowIsFound()
    {
        await using var host = await ActivityHost.CreateAsync();
        for (var i = 1; i <= 3; i++)
        {
            await host.IntentAsync(i);
        }

        Assert.Equal(3, await host.HashAsync());
        var chain = host.Provider.GetRequiredService<StoreChain<ApiMutationIntent>>();
        using (var scope = host.Provider.CreateScope())
        {
            Assert.True((await chain.VerifyAsync(scope.ServiceProvider.GetRequiredService<StoreDb>(), CancellationToken.None)).IsValid);
        }

        await host.ReadStoreAsync(db => db.Set<ApiMutationIntent>().Where(i => i.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(i => i.BodyLength, 99L)));
        using var scope2 = host.Provider.CreateScope();
        var broken = await chain.VerifyAsync(scope2.ServiceProvider.GetRequiredService<StoreDb>(), CancellationToken.None);
        Assert.Equal(AuditChainFailure.HashMismatch, broken.Failure);
        Assert.Equal(2, broken.FailedAtChainIndex);
    }

    [Fact]
    public void TheIntentLayout_IsPinned()
    {
        var intent = new ApiMutationIntent
        {
            Id = 1,
            RequestId = Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
            StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UserId = "u1",
            SessionId = "s1",
            Method = "POST",
            Path = "/api/x",
            EndpointKey = "/api/x",
            BodySha256 = "BODYSHA",
            BodyLength = 42,
        };
        using var hasher = new FieldChainHasher(ChainTestHelper.KeyA);

        Assert.Equal("FE2D65BBC17F4D4707D4E3C920C97DD8E03935946216AAD9CB14C278493581C3", hasher.Compute(null, StoreLayouts.Fields(intent)));
    }

    [Fact]
    public void AHashedRow_CannotBeHashedAgain()
    {
        var intent = new ApiMutationIntent { RequestId = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, Method = "POST", Path = "/x", BodySha256 = "a" };
        var log = new ApiRequestLog { RequestId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow, HttpMethod = "GET", Path = "/" };
        intent.SetHash(1, null, "AA");
        log.SetHash(1, null, "AA");

        Assert.Throws<InvalidOperationException>(() => intent.SetHash(2, "AA", "BB"));
        Assert.Throws<InvalidOperationException>(() => log.SetHash(2, "AA", "BB"));
    }
}

public sealed class ActivitySearchTests
{
    private static ActivitySearchQuery Query(Action<List<string>>? _ = null) => new(
        new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

    private static IActivitySearch Search(ActivityHost host) => host.Provider.GetRequiredService<IActivitySearch>();

    [Fact]
    public async Task Filters_Work_AndTheStatusClassIsARange()
    {
        await using var host = await ActivityHost.CreateAsync();
        var correlation = Guid.NewGuid();
        await host.LogAsync(1, "u1", "POST", 200, "s1", correlation, endpoint: "/a", ip: "10.0.0.1");
        await host.LogAsync(2, "u2", "GET", 404, "s2", endpoint: "/b", ip: "10.0.0.2");
        await host.LogAsync(3, "u1", "GET", 500, "s1", endpoint: "/a");
        var search = Search(host);

        async Task<long[]> Ids(ActivitySearchQuery q) => (await search.SearchAsync(q, CancellationToken.None)).Value.Items.Select(r => r.Id).ToArray();

        Assert.Equal(new long[] { 3, 2, 1 }, await Ids(Query()));
        Assert.Equal(new long[] { 3, 1 }, await Ids(Query() with { UserId = "u1" }));
        Assert.Equal(new long[] { 3, 1 }, await Ids(Query() with { SessionId = "s1" }));
        Assert.Equal(new long[] { 1 }, await Ids(Query() with { CorrelationId = correlation }));
        Assert.Equal(new long[] { 3, 1 }, await Ids(Query() with { EndpointKey = " /a " }));
        Assert.Equal(new long[] { 3, 2 }, await Ids(Query() with { Method = " get " }));
        Assert.Equal(new long[] { 2 }, await Ids(Query() with { StatusCode = 404 }));
        Assert.Equal(new long[] { 3 }, await Ids(Query() with { StatusClass = "5xx" }));
        Assert.Equal(new long[] { 2 }, await Ids(Query() with { StatusClass = "4xx" }));
        Assert.Equal(new long[] { 1 }, await Ids(Query() with { Ip = "10.0.0.1" }));
    }

    [Fact]
    public async Task Activity_IsNewestFirst_KeysetPaged_ReturnsEveryRowOnce_AndCarriesTheChainFields()
    {
        await using var host = await ActivityHost.CreateAsync();
        for (var i = 1; i <= 5; i++)
        {
            await host.LogAsync(i);
        }

        await host.HashAsync();
        var search = Search(host);
        var seen = new List<ActivityRow>();
        string? cursor = null;
        do
        {
            var page = (await search.SearchAsync(Query() with { Before = cursor, Limit = 2 }, CancellationToken.None)).Value;
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.Equal(host.Clock.GetUtcNow(), page.AsOfUtc);
        }
        while (cursor is not null);

        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, seen.Select(r => r.Id));
        Assert.All(seen, r => Assert.NotNull(r.PayloadHash));
        Assert.Equal(seen[1].PayloadHash, seen[0].PreviousHash);
    }

    [Fact]
    public async Task TheQuery_IsValidated_AndABadCursorIsRefused()
    {
        await using var host = await ActivityHost.CreateAsync();
        var search = Search(host);
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        async Task<string> Code(ActivitySearchQuery q) => (await search.SearchAsync(q, CancellationToken.None)).Error.Code;

        Assert.Equal("ChronoAudit.Validation", await Code(new ActivitySearchQuery(from, from.AddDays(32))));
        Assert.True((await search.SearchAsync(new ActivitySearchQuery(from, from.AddDays(31)), CancellationToken.None)).IsSuccess);
        Assert.True((await search.SearchAsync(new ActivitySearchQuery(from, from.AddDays(300), UserId: "u1"), CancellationToken.None)).IsSuccess);
        Assert.Equal("ChronoAudit.Validation", await Code(new ActivitySearchQuery(from, from.AddDays(367), SessionId: "s")));
        Assert.Equal("ChronoAudit.Validation", await Code(new ActivitySearchQuery(from.AddDays(1), from)));
        Assert.True((await search.SearchAsync(new ActivitySearchQuery(from, from), CancellationToken.None)).IsSuccess);
        Assert.Equal("ChronoAudit.Validation", await Code(Query() with { Limit = 0 }));
        Assert.Equal("ChronoAudit.Validation", await Code(Query() with { Limit = 201 }));
        Assert.Equal("ChronoAudit.Validation", await Code(Query() with { StatusClass = "6xx" }));
        Assert.Equal("ChronoAudit.InvalidCursor", await Code(Query() with { Before = "nope" }));
    }

    [Fact]
    public async Task IncompleteIntents_AreThoseOlderThanTheAge_WithNoResultRow()
    {
        await using var host = await ActivityHost.CreateAsync();
        var now = host.Clock.GetUtcNow();
        await host.IntentAsync(1, startedAt: now.AddMinutes(-30)); // old, no result: incomplete
        await host.IntentAsync(2, startedAt: now.AddMinutes(-30)); // old, has a result below
        await host.IntentAsync(3, startedAt: now.AddMinutes(-1)); // too young
        await host.LogAsync(2);
        var search = Search(host);

        var viaSearch = (await search.SearchAsync(Query() with { IncompleteOnly = true }, CancellationToken.None)).Value;
        var direct = await search.IncompleteIntentsAsync(10, CancellationToken.None);

        var row = Assert.Single(viaSearch.Items);
        Assert.Equal(1, row.Id);
        Assert.Equal((short)0, row.StatusCode);
        Assert.False(row.HasBody);
        Assert.Equal(1, Assert.Single(direct).Id);

        host.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(new long[] { 1, 3 }, (await search.IncompleteIntentsAsync(10, CancellationToken.None)).Select(i => i.Id));
        Assert.Single(await search.IncompleteIntentsAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task TheBody_IsReturnedOnlyAfterItsViewWasAudited()
    {
        await using var host = await ActivityHost.CreateAsync();
        await host.LogAsync(1, body: "{\"a\":1}");
        await host.LogAsync(2);
        var search = Search(host);

        var body = (await search.ViewBodyAsync(1, "auditor-1", CancellationToken.None)).Value;

        Assert.Equal("{\"a\":1}", body.Body);
        Assert.Equal("application/json", body.ContentType);
        var audit = Assert.Single(await host.AuditRowsAsync(), r => r.Action == AuditActions.ActivityBodyViewed);
        Assert.Equal("auditor-1", audit.ActorUserId);
        Assert.Equal("ApiRequestLogs", audit.ResourceType);
        Assert.Equal(body.RequestId.ToString("D"), audit.ResourceId);
        Assert.Contains("auditor-1", audit.Metadata, StringComparison.Ordinal);

        // A missing log and a log without a stored body are both "not found", and nothing is audited for them.
        Assert.Equal("ActivityLog.NotFound", (await search.ViewBodyAsync(99, "auditor-1", CancellationToken.None)).Error.Code);
        Assert.Equal("ActivityLog.NotFound", (await search.ViewBodyAsync(2, "auditor-1", CancellationToken.None)).Error.Code);
        Assert.Single(await host.AuditRowsAsync(), r => r.Action == AuditActions.ActivityBodyViewed);
    }

    [Fact]
    public async Task TheBody_IsNotReturned_WhenTheViewCannotBeAudited()
    {
        await using var noModule = await ActivityHost.CreateAsync(withAuditedModule: false);
        await noModule.LogAsync(1, body: "{\"a\":1}");
        var refused = await Search(noModule).ViewBodyAsync(1, "auditor-1", CancellationToken.None);
        Assert.Equal("ChronoAudit.AuditUnavailable", refused.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Unavailable, refused.Error.Kind);

        await using var failing = await ActivityHost.CreateAsync(customize: b => b.Services.AddScoped<IAuditLogWriter>(_ => new BrokenWriter()));
        await failing.LogAsync(1, body: "{\"a\":1}");
        var unavailable = await Search(failing).ViewBodyAsync(1, "auditor-1", CancellationToken.None);
        Assert.Equal("ChronoAudit.AuditUnavailable", unavailable.Error.Code);
        Assert.DoesNotContain(await failing.AuditRowsAsync(), r => r.Action == AuditActions.ActivityBodyViewed);
    }

    private sealed class BrokenWriter : IAuditLogWriter
    {
        public Task AppendHashedAsync(string actorUserId, string action, string resourceType, string resourceId, object? metadata = null, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("audit store down"));
    }
}
