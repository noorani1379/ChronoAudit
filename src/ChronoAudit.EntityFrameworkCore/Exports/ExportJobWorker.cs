using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

internal static class ExportMetrics
{
    public static readonly Meter Meter = new(ChronoAuditMetrics.MeterName);

    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("chronoaudit.exports.completed", description: "Export jobs that produced a complete file.");

    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("chronoaudit.exports.failed", description: "Export jobs that failed for good.");

    public static readonly Histogram<long> Rows = Meter.CreateHistogram<long>("chronoaudit.exports.rows", description: "Rows in a completed export.");
}

/// <summary>
/// Runs the background exports. One loop: take the oldest job that waits (or whose owner stopped renewing its lease), claim it against the
/// concurrency stamp (two workers, or two instances, cannot both own it), write its file batch by batch and checkpoint after each batch,
/// finish with the SHA-256 of the whole file. A recycle or a crash costs at most the batch in flight: the next claim cuts the file back to
/// the checkpointed size and continues from the checkpoint cursor. A CSV is written directly; an XLSX is first spooled as JSON lines with
/// the same checkpoint rules and converted at the end, so a workbook of a million rows never sits in memory.
/// </summary>
internal sealed class ExportJobWorker(
    ChronoAuditStoreRegistration registration, ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory, ExportOptions options, ExportFileStore files,
    TimeProvider clock, ILogger<ExportJobWorker> logger) : BackgroundService
{
    // The cursor of a job whose last batch is written: a resume then goes straight to finishing, never reads that batch twice.
    internal const string DoneCursor = "\u001fdone";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    private TimeSpan Lease => TimeSpan.FromSeconds(options.LeaseSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The export worker iteration failed; it will look at the queue again.");
            }

            if (!worked)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Takes and runs at most one job; true when there was one (the loop then looks again at once). Internal for the tests.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        var now = clock.GetUtcNow();

        var job = await db.Set<ExportJob>()
            .Where(j => j.State == ExportState.Pending || (j.State == ExportState.Running && (j.LeaseUntilUtc == null || j.LeaseUntilUtc < now)))
            .OrderBy(j => j.CreatedAtUtc).ThenBy(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            await CleanUpAsync(db, now, cancellationToken);
            return false;
        }

        job.Claim(_owner, now, Lease);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker took it first (or it was cancelled meanwhile): nothing was written, look at the queue again.
            return true;
        }

        if (job.Attempts == 1)
        {
            Note(await ExportAudit.AppendAsync(registry, scope.ServiceProvider, job.RequestedByUserId, AuditActions.ExportStarted, job.Id, new { job.Producer, Format = job.Format.ToString() }, cancellationToken));
        }

        try
        {
            await RunJobAsync(db, scope.ServiceProvider, job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The job changed under us (cancelled, or taken after a long stall): stop writing; whoever changed it owns it now.
            logger.LogWarning("Export job {JobId} changed while it ran; this worker stops.", job.Id);
            DropFilesIfCancelled(db, job.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export job {JobId} failed on attempt {Attempt}.", job.Id, job.Attempts);
            await ReleaseAsync(db, scope.ServiceProvider, job.Id, ex.Message, cancellationToken);
        }

        return true;
    }

    private async Task RunJobAsync(DbContext db, IServiceProvider services, ExportJob job, CancellationToken cancellationToken)
    {
        var producer = services.GetServices<IExportProducer>().FirstOrDefault(p => string.Equals(p.Key, job.Producer, StringComparison.Ordinal));
        if (producer is null)
        {
            await FinishFailedAsync(db, services, job, "Export.Failed", $"The export producer '{job.Producer}' is no longer registered.", cancellationToken);
            return;
        }

        using var parameters = JsonDocument.Parse(job.ParametersJson);
        var scope = new ChronoAuditScope(job.ScopeKind, job.RequestedByUserId, job.ScopeGroupId);
        var columns = await producer.ColumnsAsync(parameters.RootElement, scope, cancellationToken);
        var xlsx = job.Format == ExportFormat.Xlsx;
        var finalPath = files.PathOf(job.FileName!);
        if (finalPath is null)
        {
            await FinishFailedAsync(db, services, job, "Export.Failed", "The export file name is not valid.", cancellationToken);
            return;
        }

        files.EnsureDirectory();

        // A CSV is written straight into its final file; an XLSX is spooled first and converted when the last batch is in.
        var workPath = xlsx ? Path.ChangeExtension(finalPath, ".spool") : finalPath;
        var rowCap = xlsx ? Math.Min(options.MaxRows, ExportLimits.MaxXlsxRows) : options.MaxRows;
        await using (var file = new FileStream(workPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            if (file.Length < job.CheckpointBytes)
            {
                await FinishFailedAsync(db, services, job, "Export.FileCorrupt", "The half-written export file is missing; ask for a new export.", cancellationToken);
                return;
            }

            // Everything after the checkpoint is the batch that was in flight when the last worker stopped: cut it away and redo it.
            file.SetLength(job.CheckpointBytes);
            file.Seek(0, SeekOrigin.End);
            if (!xlsx && file.Position == 0)
            {
                await file.WriteAsync(CsvWriter.Bom.ToArray(), cancellationToken);
                await file.WriteAsync(Utf8.GetBytes(string.Join(',', columns.Select(c => CsvWriter.Escape(c.Header, isText: false))) + "\r\n"), cancellationToken);
            }

            while (job.CheckpointCursor != DoneCursor)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = await producer.NextAsync(parameters.RootElement, scope, job.CheckpointCursor, options.BatchSize, cancellationToken);
                if (next.IsFailure)
                {
                    file.Close();
                    DeleteQuietly(workPath);
                    await FinishFailedAsync(db, services, job, next.Error.Code, next.Error.Message, cancellationToken);
                    return;
                }

                var batch = next.Value;
                if (job.RowCount + batch.Rows.Count > rowCap)
                {
                    file.Close();
                    DeleteQuietly(workPath);
                    DeleteQuietly(finalPath);
                    await FinishFailedAsync(db, services, job, "Export.TooLarge", $"The export is over the limit of {rowCap:N0} rows; narrow the period or the filter.", cancellationToken);
                    return;
                }

                await WriteBatchAsync(file, batch.Rows, xlsx, cancellationToken);
                await file.FlushAsync(cancellationToken);

                // The checkpoint is saved against the concurrency stamp: a cancel, or a takeover, makes this save the conflict and the job stops.
                job.Checkpoint(batch.Done ? DoneCursor : batch.NextCursor, file.Position, batch.Rows.Count, clock.GetUtcNow(), Lease);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        if (xlsx)
        {
            await using var output = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await XlsxWriter.WriteSingleSheetAsync(output, "Export", columns, ReadSpoolAsync(workPath, cancellationToken), rightToLeft: false, cancellationToken);
        }

        var (size, sha) = await HashAsync(finalPath, cancellationToken);
        job.Complete(size, sha, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        ExportMetrics.Completed.Add(1);
        ExportMetrics.Rows.Record(job.RowCount);
        DeleteQuietly(workPath == finalPath ? string.Empty : workPath);

        // The state is saved first, so the audit row never describes a finish that did not happen.
        Note(await ExportAudit.AppendAsync(
            registry, services, job.RequestedByUserId, AuditActions.ExportFinished, job.Id,
            new { job.Producer, State = job.State.ToString(), job.RowCount, SizeBytes = size, Sha256 = sha }, cancellationToken));
    }

    private static async Task WriteBatchAsync(Stream file, IReadOnlyList<IReadOnlyList<SheetCell>> rows, bool spool, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(spool ? SpoolLine(row) : CsvLine(row)).Append(spool ? "\n" : "\r\n");
        }

        await file.WriteAsync(Utf8.GetBytes(text.ToString()), cancellationToken);
    }

    private static string CsvLine(IReadOnlyList<SheetCell> row) => string.Join(',', row.Select(CsvCell));

    private static string CsvCell(SheetCell cell) => cell.Kind switch
    {
        SheetCellKind.Number or SheetCellKind.Integer => cell.Number is { } n ? CsvWriter.Number(n) : string.Empty,
        SheetCellKind.Date => cell.Number is { } d ? CsvWriter.Instant(new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate((double)d), DateTimeKind.Utc))) : string.Empty,
        _ => CsvWriter.Escape(cell.Text, isText: true),
    };

    // One JSON array per row and one row per line: [[kind, text, number], ...]; a newline inside a text is escaped by JSON.
    private static string SpoolLine(IReadOnlyList<SheetCell> row)
        => JsonSerializer.Serialize(row.Select(c => new object?[] { (int)c.Kind, c.Text, c.Number }));

    private static async IAsyncEnumerable<IReadOnlyList<SheetCell>> ReadSpoolAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Utf8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var cells = new List<SheetCell>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var kind = (SheetCellKind)item[0].GetInt32();
                var text = item[1].ValueKind == JsonValueKind.Null ? null : item[1].GetString();
                decimal? number = item[2].ValueKind == JsonValueKind.Null ? null : item[2].GetDecimal();
                cells.Add(new SheetCell(kind, text, number));
            }

            yield return cells;
        }
    }

    private async Task FinishFailedAsync(DbContext db, IServiceProvider services, ExportJob job, string code, string message, CancellationToken cancellationToken)
    {
        job.Fail(code, message, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        ExportMetrics.Failed.Add(1);
        Note(await ExportAudit.AppendAsync(
            registry, services, job.RequestedByUserId, AuditActions.ExportFinished, job.Id, new { job.Producer, State = job.State.ToString(), job.RowCount, ErrorCode = code }, cancellationToken));
    }

    // A failed attempt goes back to the queue with its checkpoint (or fails for good after the last attempt). Reloaded first: the tracked copy may be stale.
    private async Task ReleaseAsync(DbContext db, IServiceProvider services, Guid jobId, string message, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var job = await db.Set<ExportJob>().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null || job.IsTerminal)
        {
            return;
        }

        job.Release(message, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Export job {JobId} changed while it was released; the other change wins.", jobId);
            return;
        }

        if (job.State == ExportState.Failed)
        {
            ExportMetrics.Failed.Add(1);
            Note(await ExportAudit.AppendAsync(
                registry, services, job.RequestedByUserId, AuditActions.ExportFinished, job.Id, new { job.Producer, State = job.State.ToString(), job.RowCount, job.ErrorCode }, cancellationToken));
        }
    }

    // A running job that its owner cancelled: its half-written files are of no use any more.
    private void DropFilesIfCancelled(DbContext db, Guid jobId)
    {
        db.ChangeTracker.Clear();
        var job = db.Set<ExportJob>().AsNoTracking().FirstOrDefault(j => j.Id == jobId);
        if (job is { State: ExportState.Cancelled, FileName: not null })
        {
            files.Delete(job.FileName);
            files.Delete(Path.ChangeExtension(job.FileName, ".spool"));
        }
    }

    private static async Task<(long Size, string Sha256)> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return (stream.Length, Convert.ToHexString(hash));
    }

    private static void DeleteQuietly(string path)
    {
        if (path.Length > 0 && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // Once an hour: delete the files of jobs that finished before the retention period. The rows stay; a download then says the file is gone.
    private async Task CleanUpAsync(DbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now - _lastCleanup < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastCleanup = now;
        var cutoff = now.AddDays(-options.RetentionDays);
        const int page = 500;
        for (var skip = 0; ; skip += page)
        {
            var names = await db.Set<ExportJob>().AsNoTracking()
                .Where(j => j.FileName != null && j.FinishedAtUtc != null && j.FinishedAtUtc < cutoff)
                .OrderBy(j => j.FinishedAtUtc).ThenBy(j => j.Id)
                .Skip(skip).Take(page)
                .Select(j => j.FileName!)
                .ToListAsync(cancellationToken);
            foreach (var name in names)
            {
                files.Delete(name);
                files.Delete(Path.ChangeExtension(name, ".spool"));
            }

            if (names.Count < page)
            {
                return;
            }
        }
    }

    private void Note(ExportAuditOutcome outcome)
    {
        if (outcome == ExportAuditOutcome.Failed)
        {
            logger.LogWarning("The audit row of an export step could not be written.");
        }
    }
}
