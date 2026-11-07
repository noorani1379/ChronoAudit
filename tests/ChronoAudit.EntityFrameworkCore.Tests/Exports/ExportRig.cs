using System.Text.Json;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>What the scripted producer does; one instance per rig, shared by every scope.</summary>
public sealed class ScriptedState
{
    public int Batches { get; set; } = 3;

    public int RowsPerBatch { get; set; } = 2;

    /// <summary>The call (1-based, counted over the rig's life) that throws once.</summary>
    public int FailOnCall { get; set; } = -1;

    /// <summary>When true every call throws.</summary>
    public bool AlwaysFail { get; set; }

    /// <summary>Called with the batch index before the rows are returned (a hook to cancel a job mid-run).</summary>
    public Func<int, Task>? OnBatch { get; set; }

    public int Calls { get; set; }

    public List<string?> Cursors { get; } = [];
}

/// <summary>A host producer that yields fixed batches and can fail at a chosen call, to prove the resume.</summary>
public sealed class ScriptedProducer(ScriptedState state) : IExportProducer
{
    public string Key => "scripted";

    public bool RequiresAllScope => false;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult(ChronoAuditResult.Success());

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>([new SheetColumn("n")]);

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        state.Cursors.Add(cursor);
        state.Calls++;
        if (state.AlwaysFail || state.Calls == state.FailOnCall)
        {
            throw new InvalidOperationException("the source dropped");
        }

        var index = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        if (state.OnBatch is { } hook)
        {
            await hook(index);
        }

        var rows = Enumerable.Range(0, state.RowsPerBatch).Select(i => (IReadOnlyList<SheetCell>)[SheetCell.Of($"row-{index}-{i}")]).ToList();
        var done = index + 1 >= state.Batches;
        return new ExportBatchRows(rows, done ? null : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), done);
    }
}

/// <summary>An audit writer that always fails, to prove that a download without its audit row returns nothing.</summary>
public sealed class ThrowingAuditWriter : IAuditLogWriter
{
    public Task AppendHashedAsync(string actorUserId, string action, string resourceType, string resourceId, object? metadata = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the audit store is down");
}

/// <summary>A host with the exports, a worker, a temp folder and the scripted producer.</summary>
internal sealed class ExportRig : IAsyncDisposable
{
    public static readonly ChronoAuditScope Me = ChronoAuditScope.OwnOf("u1");
    public static readonly ChronoAuditScope Admin = ChronoAuditScope.Everything("admin");

    private ExportRig(ActivityHost host, string folder, ScriptedState script)
    {
        Host = host;
        Folder = folder;
        Script = script;
    }

    public ActivityHost Host { get; }

    public string Folder { get; }

    public ScriptedState Script { get; }

    public IExportService Service => Host.Provider.GetRequiredService<IExportService>();

    public ExportJobWorker Worker => Host.Provider.GetRequiredService<ExportJobWorker>();

    public static async Task<ExportRig> CreateAsync(
        Action<ExportOptions>? configure = null, bool withAuditedModule = true, bool throwingAudit = false, Action<ChronoAuditBuilder>? more = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "chronoaudit-exports-" + Guid.NewGuid().ToString("N"));
        var script = new ScriptedState();
        var host = await ActivityHost.CreateAsync(
            configure: o =>
            {
                o.Exports.Directory = folder;
                configure?.Invoke(o.Exports);
            },
            withAuditedModule: withAuditedModule,
            customize: b =>
            {
                b.Services.AddSingleton(script);
                b.AddBuiltInAnalytics().AddSavedReports().AddExports().AddExportProducer<ScriptedProducer>().AddExportWorker();
                if (throwingAudit)
                {
                    b.Services.AddScoped<IAuditLogWriter, ThrowingAuditWriter>();
                }

                more?.Invoke(b);
            });
        host.Clock.Set(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        return new ExportRig(host, folder, script);
    }

    public static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async Task<ExportJobDto> RequestAsync(string producer = "scripted", ExportFormat format = ExportFormat.Csv, string parameters = "{}", ChronoAuditScope? scope = null)
    {
        var result = await Service.RequestAsync(new ExportRequest(producer, format, Json(parameters)), scope ?? Me, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code + ": " + result.Error.Message : null);
        return result.Value;
    }

    /// <summary>Runs the worker until it finds nothing to do (a released job is run again); returns how many jobs it ran.</summary>
    public async Task<int> DrainAsync(int max = 20)
    {
        var ran = 0;
        while (ran < max && await Worker.RunOnceAsync(CancellationToken.None))
        {
            ran++;
        }

        return ran;
    }

    public Task<ExportJob> JobAsync(Guid id) => Host.ReadStoreAsync(db => db.Set<ExportJob>().AsNoTracking().SingleAsync(j => j.Id == id));

    public async Task ChangeJobAsync(Guid id, Action<ExportJob> change)
    {
        using var scope = Host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
        change(await db.Set<ExportJob>().SingleAsync(j => j.Id == id));
        await db.SaveChangesAsync();
    }

    public string PathOf(ExportJob job) => Path.Combine(Folder, job.FileName!);

    public async Task<IReadOnlyList<string>> AuditActionsAsync() => (await Host.AuditRowsAsync()).Select(a => a.Action).ToList();

    // Tests poke rows with ExecuteUpdate: the entity keeps its setters private on purpose.
    public async Task ForceRunningWithDeadLeaseAsync(Guid id)
    {
        var dead = Host.Clock.GetUtcNow().AddMinutes(-1);
        using var scope = Host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
        await db.Set<ExportJob>().Where(j => j.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, ExportState.Running)
            .SetProperty(j => j.LeaseOwner, "dead-worker")
            .SetProperty(j => j.LeaseUntilUtc, dead));
    }

    public async Task SetProducerAsync(Guid id, string producer)
    {
        using var scope = Host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
        await db.Set<ExportJob>().Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Producer, producer));
    }

    public async Task SetFinishedAsync(Guid id, DateTimeOffset finishedAt)
    {
        using var scope = Host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
        await db.Set<ExportJob>().Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.FinishedAtUtc, finishedAt));
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}
