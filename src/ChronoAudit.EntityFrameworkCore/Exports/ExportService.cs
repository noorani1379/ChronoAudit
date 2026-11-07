using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Where the export files live and how the worker behaves (section <c>ChronoAudit:Exports</c>).</summary>
public sealed class ExportOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Exports";

    /// <summary>
    /// The folder of the export files, created on first use. A relative path is resolved against the application's base directory
    /// (never the current directory, which is a system folder under IIS). Default <c>App_Data/chronoaudit-exports</c>.
    /// </summary>
    public string Directory { get; set; } = "App_Data/chronoaudit-exports";

    /// <summary>How many days a finished file is kept before the worker deletes it (1 to 365). The job row stays; a download then says the file is gone.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>How long a claim is valid without a checkpoint, in seconds (30 to 3600); a worker that stops renewing loses the job to another.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>The pause between looks at an empty queue, in seconds (1 to 300).</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>The most rows one export may hold (at least 1); more fails the job with <c>Export.TooLarge</c>.</summary>
    public long MaxRows { get; set; } = 5_000_000;

    /// <summary>The most Pending or Running jobs one user may have at a time (1 to 50).</summary>
    public int MaxActivePerUser { get; set; } = 3;

    /// <summary>The most rows asked from a producer per batch (100 to 5000).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            throw new InvalidOperationException("ChronoAudit:Exports:Directory must not be empty.");
        }

        if (RetentionDays is < 1 or > 365 || LeaseSeconds is < 30 or > 3600 || PollIntervalSeconds is < 1 or > 300
            || MaxRows < 1 || MaxActivePerUser is < 1 or > 50 || BatchSize is < 100 or > 5000)
        {
            throw new InvalidOperationException(
                "ChronoAudit:Exports: RetentionDays 1-365, LeaseSeconds 30-3600, PollIntervalSeconds 1-300, MaxRows at least 1, MaxActivePerUser 1-50, BatchSize 100-5000.");
        }
    }
}

/// <summary>What a caller sends to ask for an export.</summary>
/// <param name="Producer">The producer key (for example <c>analytics</c>).</param>
/// <param name="Format">The file format.</param>
/// <param name="Parameters">The producer parameters (a JSON object).</param>
public sealed record ExportRequest(string Producer, ExportFormat Format, JsonElement Parameters);

/// <summary>An export job as a caller sees it.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Producer">The producer key.</param>
/// <param name="Format">The file format.</param>
/// <param name="State">The state.</param>
/// <param name="RowCount">The rows written so far.</param>
/// <param name="FileSizeBytes">The file size, once completed.</param>
/// <param name="Sha256">The SHA-256 of the file (upper-case hex), once completed.</param>
/// <param name="ErrorCode">The stable error code of a failed job.</param>
/// <param name="ErrorMessage">The message of the last failure.</param>
/// <param name="CreatedAtUtc">When it was requested.</param>
/// <param name="StartedAtUtc">When a worker first took it.</param>
/// <param name="FinishedAtUtc">When it reached a terminal state.</param>
public sealed record ExportJobDto(
    Guid Id, string Producer, ExportFormat Format, ExportState State, long RowCount, long? FileSizeBytes, string? Sha256, string? ErrorCode, string? ErrorMessage,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc);

/// <summary>A finished file ready to stream. The caller disposes <see cref="Content"/>.</summary>
/// <param name="Content">The file stream.</param>
/// <param name="DownloadName">The suggested file name.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="Sha256">The SHA-256 of the file, to show the user.</param>
/// <param name="SizeBytes">The file size.</param>
public sealed record ExportDownload(Stream Content, string DownloadName, string ContentType, string Sha256, long SizeBytes);

/// <summary>Asks for, follows, cancels and downloads background exports. A job is private to the user who asked for it.</summary>
public interface IExportService
{
    /// <summary>Queues an export. Nothing runs in the request: the job waits for the worker.</summary>
    /// <param name="request">What to export.</param>
    /// <param name="scope">The requester's scope; a snapshot of it is stored with the job.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The pending job, or <c>Export.UnknownProducer</c>, <c>Export.KindForbidden</c>, <c>Export.TooManyActive</c>, or the producer's refusal.</returns>
    Task<ChronoAuditResult<ExportJobDto>> RequestAsync(ExportRequest request, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Reads one of the caller's jobs.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="scope">The caller's scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The job, or <c>Export.NotFound</c> (also for another user's job).</returns>
    Task<ChronoAuditResult<ExportJobDto>> GetAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Lists the caller's jobs, newest first.</summary>
    /// <param name="scope">The caller's scope.</param>
    /// <param name="limit">How many to return; clamped to 1..50.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The jobs.</returns>
    Task<ChronoAuditResult<IReadOnlyList<ExportJobDto>>> ListAsync(ChronoAuditScope scope, int limit, CancellationToken cancellationToken);

    /// <summary>Cancels one of the caller's jobs that has not finished.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="scope">The caller's scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The cancelled job, or <c>Export.NotFound</c>, <c>Export.AlreadyFinished</c>, <c>Export.Conflict</c>.</returns>
    Task<ChronoAuditResult<ExportJobDto>> CancelAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the file of a completed job. The download is recorded in the audit table first (evidence before the bytes leave); every
    /// download is its own audit row.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="scope">The caller's scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The file, or <c>Export.NotFound</c>, <c>Export.NotReady</c>, <c>Export.FileMissing</c>, <c>ChronoAudit.AuditUnavailable</c>.</returns>
    Task<ChronoAuditResult<ExportDownload>> DownloadAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken);
}

/// <summary>Opens a finished file. The name comes from the job row, never from a request, and must stay inside the folder.</summary>
internal sealed class ExportFileStore(ExportOptions options)
{
    /// <summary>The full path of a file name, or null when it would leave the export folder.</summary>
    public string? PathOf(string fileName)
    {
        var root = RootPath();
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    public Stream? OpenRead(string fileName)
    {
        var path = PathOf(fileName);
        return path is not null && File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
    }

    public bool Exists(string fileName) => PathOf(fileName) is { } path && File.Exists(path);

    public void Delete(string fileName)
    {
        if (PathOf(fileName) is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string EnsureDirectory()
    {
        var root = RootPath();
        System.IO.Directory.CreateDirectory(root);
        return root;
    }

    private string RootPath() => Path.GetFullPath(Path.IsPathRooted(options.Directory) ? options.Directory : Path.Combine(AppContext.BaseDirectory, options.Directory));
}

/// <summary>Writes the audit rows of the export life cycle through the first audited module.</summary>
internal enum ExportAuditOutcome
{
    Written,
    Skipped,
    Failed,
}

internal static class ExportAudit
{
    public static async Task<ExportAuditOutcome> AppendAsync(
        ChronoAuditRegistry registry, IServiceProvider services, string actor, string action, Guid jobId, object metadata, CancellationToken cancellationToken)
    {
        if (registry.Modules.Count == 0)
        {
            return ExportAuditOutcome.Skipped;
        }

        try
        {
            var audit = (AuditableDbContext)services.GetRequiredService(registry.Modules[0].ContextType);
            await services.GetRequiredService<IAuditLogWriter>().AppendHashedAsync(actor, action, nameof(ExportJob), jobId.ToString("N"), metadata, cancellationToken);
            await audit.SaveChangesAsync(cancellationToken);
            return ExportAuditOutcome.Written;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            services.GetRequiredService<IAuditLogAccessor>().Clear();
            return ExportAuditOutcome.Failed;
        }
    }
}

internal sealed class ExportService(
    ChronoAuditStoreRegistration registration, ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory, ExportOptions options, ExportFileStore files,
    TimeProvider clock, ILogger<ExportService> logger) : IExportService
{
    private const string CsvType = "text/csv; charset=utf-8";
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly ChronoAuditError NotFound = ChronoAuditError.NotFound("Export.NotFound", "The export was not found.");
    private static readonly ChronoAuditError Conflict = ChronoAuditError.Conflict("Export.Conflict", "The export changed at the same time; try again.");
    private int _warned;

    public async Task<ChronoAuditResult<ExportJobDto>> RequestAsync(ExportRequest request, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.UserId is null)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "An export needs a signed-in user.");
        }

        if (!Enum.IsDefined(request.Format))
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The export format is not valid.");
        }

        var parameters = request.Parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : request.Parameters.GetRawText();
        if (parameters.Length > ExportLimits.MaxParametersLength)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"The export parameters may have at most {ExportLimits.MaxParametersLength} characters.");
        }

        using var serviceScope = scopeFactory.CreateScope();
        var producer = serviceScope.ServiceProvider.GetServices<IExportProducer>().FirstOrDefault(p => string.Equals(p.Key, request.Producer, StringComparison.Ordinal));
        if (producer is null)
        {
            return ChronoAuditError.Validation("Export.UnknownProducer", "There is no export with this producer key.");
        }

        if (producer.RequiresAllScope && scope.Kind != ScopeKind.All)
        {
            return ChronoAuditError.Forbidden("Export.KindForbidden", "This export needs the All scope.");
        }

        // The producer's checks run now: a job is allowed when queued, so the worker only executes.
        using var document = JsonDocument.Parse(parameters);
        var valid = await producer.ValidateAsync(document.RootElement, scope, cancellationToken);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var db = Db(serviceScope);
        var userId = scope.UserId;
        if (await ActiveCount(db, userId).CountAsync(cancellationToken) >= options.MaxActivePerUser)
        {
            return TooManyActive();
        }

        var job = ExportJob.Create(userId, producer.Key, request.Format, parameters, scope.Kind, scope.GroupId, clock.GetUtcNow());
        db.Set<ExportJob>().Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // Two requests that both passed the check above can overshoot: count again after the insert and take our own job back if the
        // limit is exceeded. In that race both may step back; the limit is never exceeded.
        if (await ActiveCount(db, userId).CountAsync(cancellationToken) > options.MaxActivePerUser)
        {
            db.Set<ExportJob>().Remove(job);
            await db.SaveChangesAsync(cancellationToken);
            return TooManyActive();
        }

        return ToDto(job);
    }

    public async Task<ChronoAuditResult<ExportJobDto>> GetAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var job = await Mine(Db(serviceScope).Set<ExportJob>().AsNoTracking(), scope).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        return job is null ? NotFound : ToDto(job);
    }

    public async Task<ChronoAuditResult<IReadOnlyList<ExportJobDto>>> ListAsync(ChronoAuditScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var jobs = await Mine(Db(serviceScope).Set<ExportJob>().AsNoTracking(), scope)
            .OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id)
            .Take(Math.Clamp(limit, 1, ExportLimits.MaxListed))
            .ToListAsync(cancellationToken);
        IReadOnlyList<ExportJobDto> list = jobs.Select(ToDto).ToList();
        return ChronoAuditResult.Success(list);
    }

    public async Task<ChronoAuditResult<ExportJobDto>> CancelAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var db = Db(serviceScope);
        var job = await Mine(db.Set<ExportJob>(), scope).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
        {
            return NotFound;
        }

        var cancelled = job.Cancel(clock.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        // The worker saves its checkpoints against the same stamp: if it wrote one first, this save is the conflict and the caller asks again.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict;
        }

        var audit = await ExportAudit.AppendAsync(registry, serviceScope.ServiceProvider, job.RequestedByUserId, AuditActions.ExportCancelled, job.Id, new { job.Producer, job.RowCount }, cancellationToken);
        Note(audit, "cancel");
        return ToDto(job);
    }

    public async Task<ChronoAuditResult<ExportDownload>> DownloadAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var job = await Mine(Db(serviceScope).Set<ExportJob>().AsNoTracking(), scope).FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
        {
            return NotFound;
        }

        if (job.State != ExportState.Completed || job.FileName is null || job.Sha256 is null || job.FileSizeBytes is null)
        {
            return ChronoAuditError.Conflict("Export.NotReady", "The file of this export is not ready or was not produced.");
        }

        var fileMissing = ChronoAuditError.NotFound("Export.FileMissing", "The file of this export is no longer available; ask for a new export.");
        if (!files.Exists(job.FileName))
        {
            return fileMissing;
        }

        // Evidence first: the download is recorded in the audit table before the bytes leave. Two downloads are two rows.
        var audit = await ExportAudit.AppendAsync(
            registry, serviceScope.ServiceProvider, job.RequestedByUserId, AuditActions.ExportDownloaded, job.Id, new { job.Producer, job.Sha256, SizeBytes = job.FileSizeBytes }, cancellationToken);
        if (audit == ExportAuditOutcome.Failed)
        {
            return ChronoAuditError.Unavailable("ChronoAudit.AuditUnavailable", "The download could not be recorded, so nothing is returned; try again.");
        }

        Note(audit, "download");
        var stream = files.OpenRead(job.FileName);
        if (stream is null)
        {
            return fileMissing;
        }

        var xlsx = job.Format == ExportFormat.Xlsx;
        var name = $"{job.Producer}-{job.CreatedAtUtc.UtcDateTime:yyyyMMdd-HHmmss}.{(xlsx ? "xlsx" : "csv")}";
        return new ExportDownload(stream, name, xlsx ? XlsxType : CsvType, job.Sha256, job.FileSizeBytes.Value);
    }

    private static IQueryable<ExportJob> ActiveCount(DbContext db, string userId)
        => db.Set<ExportJob>().AsNoTracking().Where(j => j.RequestedByUserId == userId && (j.State == ExportState.Pending || j.State == ExportState.Running));

    // A job is private to its requester: another user's job is "not found", whatever their scope.
    private static IQueryable<ExportJob> Mine(IQueryable<ExportJob> jobs, ChronoAuditScope scope)
    {
        var user = scope.UserId;
        return jobs.Where(j => j.RequestedByUserId == user);
    }

    private ChronoAuditError TooManyActive()
        => ChronoAuditError.Conflict("Export.TooManyActive", $"You can have at most {options.MaxActivePerUser} exports waiting or running at a time; wait for one to finish or cancel it.");

    private static ExportJobDto ToDto(ExportJob j) => new(
        j.Id, j.Producer, j.Format, j.State, j.RowCount, j.FileSizeBytes, j.Sha256, j.ErrorCode, j.ErrorMessage, j.CreatedAtUtc, j.StartedAtUtc, j.FinishedAtUtc);

    private DbContext Db(IServiceScope serviceScope) => (DbContext)serviceScope.ServiceProvider.GetRequiredService(registration.ContextType);

    private void Note(ExportAuditOutcome outcome, string what)
    {
        if (outcome == ExportAuditOutcome.Skipped && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning("No audited module is registered, so export {Step} steps are not recorded in the audit table.", what);
        }
        else if (outcome == ExportAuditOutcome.Failed)
        {
            logger.LogWarning("The audit row of an export {Step} could not be written.", what);
        }
    }
}
