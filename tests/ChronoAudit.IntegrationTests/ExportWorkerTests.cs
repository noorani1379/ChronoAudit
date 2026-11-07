using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

/// <summary>What the scripted producer does; one instance per test database, shared by every scope.</summary>
public sealed class WorkerScript
{
    private int _calls;

    public int Batches { get; init; } = 5;

    public int RowsPerBatch { get; init; } = 3;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Called with the batch index before the rows are returned (a hook to kill the worker mid-run).</summary>
    public Func<int, Task>? OnBatch { get; set; }

    public ConcurrentQueue<string?> Cursors { get; } = new();

    public int Next() => Interlocked.Increment(ref _calls);

    public int ExpectedRows => Batches * RowsPerBatch;
}

/// <summary>A host producer that yields fixed batches, so a test knows exactly which rows a complete file holds.</summary>
public sealed class WorkerScriptProducer(WorkerScript script) : IExportProducer
{
    public string Key => "scripted";

    public bool RequiresAllScope => false;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult(ChronoAuditResult.Success());

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>([new SheetColumn("n")]);

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        script.Next();
        script.Cursors.Enqueue(cursor);

        var index = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        if (script.OnBatch is { } hook)
        {
            await hook(index);
        }

        // A little work per batch, so concurrent workers really overlap instead of running one after the other.
        await Task.Delay(5, CancellationToken.None);

        var rows = Enumerable.Range(0, script.RowsPerBatch).Select(i => (IReadOnlyList<SheetCell>)[SheetCell.Of($"row-{index}-{i}")]).ToList();
        var done = index + 1 >= script.Batches;
        return new ExportBatchRows(rows, done ? null : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), done);
    }
}

/// <summary>
/// H18 through the real worker on a real database: the claim race, a lost worker and the resume. The tests of <c>ExportClaimTests</c>
/// prove the concurrency token; these prove that the worker's own claim query and checkpoint logic use it correctly.
/// </summary>
public abstract class ExportWorkerTestsBase(DatabaseProvider provider)
{
    private static readonly string[] Users = ["u1", "u2", "u3", "u4", "u5", "u6"];

    private async Task<(TestDatabase Db, WorkerScript Script, string Folder, ExportJobWorker Worker)> StartAsync(Action<ExportOptions>? configure = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "chronoaudit-it-exports-" + Guid.NewGuid().ToString("N"));
        var script = new WorkerScript();
        var db = await TestDatabase.CreateAsync(
            provider,
            configureOptions: o =>
            {
                o.Exports.Directory = folder;
                o.Exports.MaxActivePerUser = 50;
                configure?.Invoke(o.Exports);
            },
            customize: b =>
            {
                b.Services.AddSingleton(script);
                b.AddExportProducer<WorkerScriptProducer>().AddExportWorker();
            });
        return (db, script, folder, db.Services.GetRequiredService<ExportJobWorker>());
    }

    private static async Task<Guid> RequestAsync(TestDatabase db, string user)
    {
        using var scope = db.Scope();
        var result = await scope.ServiceProvider.GetRequiredService<IExportService>()
            .RequestAsync(new ExportRequest("scripted", ExportFormat.Csv, JsonDocument.Parse("{}").RootElement.Clone()), ChronoAuditScope.OwnOf(user), CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code + ": " + result.Error.Message : null);
        return result.Value.Id;
    }

    private static async Task<ExportJob> JobAsync(TestDatabase db, Guid id)
    {
        using var scope = db.Scope();
        return await db.Store(scope).ExportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    // The file of a complete job: a header and every row exactly once, and the SHA-256 the job stored is the one of those bytes.
    private static void AssertCompleteFile(string folder, ExportJob job, WorkerScript script)
    {
        Assert.Equal(ExportState.Completed, job.State);
        var path = Path.Combine(folder, job.FileName!);
        var lines = File.ReadAllLines(path).Skip(1).Select(l => l.Trim('"')).ToList();

        var expected = Enumerable.Range(0, script.Batches).SelectMany(b => Enumerable.Range(0, script.RowsPerBatch).Select(i => $"row-{b}-{i}")).ToList();
        Assert.Equal(expected, lines);
        Assert.Equal(script.ExpectedRows, job.RowCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), job.Sha256, ignoreCase: true);
    }

    protected async Task VerifyConcurrentWorkers_RunEveryJobExactlyOnceAsync()
    {
        var (db, script, folder, worker) = await StartAsync();
        await using var _ = db;
        try
        {
            var ids = new List<Guid>();
            foreach (var user in Users)
            {
                ids.Add(await RequestAsync(db, user));
            }

            // Four workers, six jobs: every worker looks at the same first job at the same moment.
            var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (await worker.RunOnceAsync(CancellationToken.None))
                {
                }
            }));
            await Task.WhenAll(workers);

            foreach (var id in ids)
            {
                var job = await JobAsync(db, id);
                Assert.Equal(1, job.Attempts);
                AssertCompleteFile(folder, job, script);
            }

            // Nothing was produced twice: a lost race must not have read any batch.
            Assert.Equal(Users.Length * script.Batches, script.Calls);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    protected async Task VerifyALostWorker_LeavesALiveLease_ThenTheNextWorkerResumesAsync()
    {
        var (db, script, folder, worker) = await StartAsync();
        await using var _ = db;
        try
        {
            var id = await RequestAsync(db, "u1");

            // The first worker dies while it reads batch 2: batches 0 and 1 are checkpointed, batch 2 is not.
            using var kill = new CancellationTokenSource();
            script.OnBatch = index =>
            {
                if (index == 2)
                {
                    kill.Cancel();
                }

                return Task.CompletedTask;
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(kill.Token));
            script.OnBatch = null;

            var lost = await JobAsync(db, id);
            Assert.Equal(ExportState.Running, lost.State);
            Assert.Equal(1, lost.Attempts);
            Assert.Equal("2", lost.CheckpointCursor);
            Assert.Equal(2 * script.RowsPerBatch, lost.RowCount);
            var callsAfterTheCrash = script.Calls;

            // While the lease is live nobody may take the job.
            Assert.False(await worker.RunOnceAsync(CancellationToken.None));
            Assert.Equal(callsAfterTheCrash, script.Calls);
            Assert.Equal(1, (await JobAsync(db, id)).Attempts);

            // Once the lease has run out the next worker resumes at the checkpoint: no batch is read or written twice.
            db.Clock.Advance(TimeSpan.FromSeconds(121));
            Assert.True(await worker.RunOnceAsync(CancellationToken.None));

            var done = await JobAsync(db, id);
            Assert.Equal(2, done.Attempts);
            AssertCompleteFile(folder, done, script);
            Assert.Equal("2", script.Cursors.Skip(callsAfterTheCrash).First());
            Assert.Equal(callsAfterTheCrash + script.Batches - 2, script.Calls);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerExportWorkerTests : ExportWorkerTestsBase
{
    public SqlServerExportWorkerTests()
        : base(DatabaseProvider.SqlServer)
    {
    }

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ConcurrentWorkers_RunEveryJobExactlyOnce() => VerifyConcurrentWorkers_RunEveryJobExactlyOnceAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ALostWorker_LeavesALiveLease_ThenTheNextWorkerResumes() => VerifyALostWorker_LeavesALiveLease_ThenTheNextWorkerResumesAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlExportWorkerTests : ExportWorkerTestsBase
{
    public PostgreSqlExportWorkerTests()
        : base(DatabaseProvider.PostgreSql)
    {
    }

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ConcurrentWorkers_RunEveryJobExactlyOnce() => VerifyConcurrentWorkers_RunEveryJobExactlyOnceAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ALostWorker_LeavesALiveLease_ThenTheNextWorkerResumes() => VerifyALostWorker_LeavesALiveLease_ThenTheNextWorkerResumesAsync();
}
