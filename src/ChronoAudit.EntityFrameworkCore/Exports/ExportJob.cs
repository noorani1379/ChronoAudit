namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The file format of an export.</summary>
public enum ExportFormat : short
{
    /// <summary>Comma separated text with a UTF-8 byte order mark.</summary>
    Csv = 1,

    /// <summary>An Excel workbook with one sheet.</summary>
    Xlsx = 2,
}

/// <summary>Where an export job is in its life.</summary>
public enum ExportState : short
{
    /// <summary>Waiting for a worker (also a job released after a failed attempt).</summary>
    Pending = 1,

    /// <summary>A worker owns it under a lease.</summary>
    Running = 2,

    /// <summary>The file is complete.</summary>
    Completed = 3,

    /// <summary>It failed for good.</summary>
    Failed = 4,

    /// <summary>The requester cancelled it.</summary>
    Cancelled = 5,
}

/// <summary>
/// A big export that runs in the background. The request is a snapshot (parameters and the requester's scope at that moment); a worker
/// claims the job with a lease, writes the file in batches and records a checkpoint after each (the cursor AND the number of bytes that
/// are good), so a recycle or a crash resumes from the checkpoint: the file is cut back to the checkpointed size and continues. A job is
/// operational state, not evidence: it is excluded from the automatic audit (the start, the end with the SHA-256 and every download are
/// audit rows of their own). Every mutating method renews <see cref="ConcurrencyStamp"/>, so the claim, every checkpoint and every state
/// change are saved against it: two workers cannot own one job.
/// </summary>
public sealed class ExportJob : IExcludedFromAutoAudit
{
    /// <summary>The longest stored error message.</summary>
    public const int MaxErrorLength = 500;

    /// <summary>A job that fails this many attempts is Failed for good.</summary>
    public const int MaxAttempts = 5;

    private ExportJob()
    {
    }

    /// <summary>The job id.</summary>
    public Guid Id { get; private set; }

    /// <summary>The user who asked for the export; only this user reads, cancels or downloads it.</summary>
    public string RequestedByUserId { get; private set; } = string.Empty;

    /// <summary>The producer key (for example <c>analytics</c>).</summary>
    public string Producer { get; private set; } = string.Empty;

    /// <summary>The file format.</summary>
    public ExportFormat Format { get; private set; }

    /// <summary>The producer parameters as JSON.</summary>
    public string ParametersJson { get; private set; } = "{}";

    /// <summary>The kind of the requester's data scope at request time.</summary>
    public ScopeKind ScopeKind { get; private set; }

    /// <summary>The requester's group at request time, when the scope had one.</summary>
    public string? ScopeGroupId { get; private set; }

    /// <summary>The state.</summary>
    public ExportState State { get; private set; }

    /// <summary>Where the producer continues (opaque to the worker); null before the first batch.</summary>
    public string? CheckpointCursor { get; private set; }

    /// <summary>Bytes of the file that are good as of the last checkpoint; a resume cuts the file back to this size.</summary>
    public long CheckpointBytes { get; private set; }

    /// <summary>The rows written so far.</summary>
    public long RowCount { get; private set; }

    /// <summary>The file name inside the export directory (never a path from a request).</summary>
    public string? FileName { get; private set; }

    /// <summary>The size of the finished file.</summary>
    public long? FileSizeBytes { get; private set; }

    /// <summary>The SHA-256 of the finished file, in upper-case hex.</summary>
    public string? Sha256 { get; private set; }

    /// <summary>The worker that owns the job.</summary>
    public string? LeaseOwner { get; private set; }

    /// <summary>When the lease ends; after that another worker may take the job.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; private set; }

    /// <summary>How many times a worker claimed the job.</summary>
    public int Attempts { get; private set; }

    /// <summary>A stable error code when the job failed (<c>Export.TooLarge</c>, <c>Export.Failed</c>), else null.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>The error message of the last failure.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>When the job was requested.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When a worker first claimed it.</summary>
    public DateTimeOffset? StartedAtUtc { get; private set; }

    /// <summary>When it reached a terminal state.</summary>
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>Renewed by every change; a save whose stamp moved meanwhile is a concurrency failure.</summary>
    public Guid ConcurrencyStamp { get; private set; }

    /// <summary>True when the job is Completed, Failed or Cancelled.</summary>
    public bool IsTerminal => State is ExportState.Completed or ExportState.Failed or ExportState.Cancelled;

    /// <summary>Creates a pending job.</summary>
    /// <param name="requestedByUserId">The requester.</param>
    /// <param name="producer">The producer key.</param>
    /// <param name="format">The file format.</param>
    /// <param name="parametersJson">The producer parameters.</param>
    /// <param name="scopeKind">The requester's scope kind.</param>
    /// <param name="scopeGroupId">The requester's group, if any.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>The job.</returns>
    public static ExportJob Create(string requestedByUserId, string producer, ExportFormat format, string parametersJson, ScopeKind scopeKind, string? scopeGroupId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedByUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentNullException.ThrowIfNull(parametersJson);
        return new ExportJob
        {
            Id = Guid.NewGuid(),
            RequestedByUserId = requestedByUserId,
            Producer = producer,
            Format = format,
            ParametersJson = parametersJson,
            ScopeKind = scopeKind,
            ScopeGroupId = scopeGroupId,
            State = ExportState.Pending,
            CreatedAtUtc = now.ToUniversalTime(),
            ConcurrencyStamp = Guid.NewGuid(),
        };
    }

    /// <summary>True when a worker may take the job now: it waits, or its owner stopped renewing the lease.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>Whether the job is claimable.</returns>
    public bool IsClaimable(DateTimeOffset now)
        => State == ExportState.Pending || (State == ExportState.Running && (LeaseUntilUtc is null || LeaseUntilUtc < now.ToUniversalTime()));

    /// <summary>Takes the job under a lease and counts the attempt.</summary>
    /// <param name="owner">The worker id.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="lease">How long the lease lasts.</param>
    /// <exception cref="InvalidOperationException">The job is not claimable.</exception>
    public void Claim(string owner, DateTimeOffset now, TimeSpan lease)
    {
        var nowUtc = now.ToUniversalTime();
        if (!IsClaimable(nowUtc))
        {
            throw new InvalidOperationException("The export job is not claimable.");
        }

        State = ExportState.Running;
        LeaseOwner = owner;
        LeaseUntilUtc = nowUtc + lease;
        StartedAtUtc ??= nowUtc;
        FileName ??= Id.ToString("N") + (Format == ExportFormat.Xlsx ? ".xlsx" : ".csv");
        Attempts++;
        Renew();
    }

    /// <summary>One good batch: the cursor, the bytes and the rows move together, and the lease is renewed in the same save.</summary>
    /// <param name="cursor">Where the producer continues.</param>
    /// <param name="bytes">The good bytes of the file.</param>
    /// <param name="rowsAdded">The rows of this batch.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="lease">The lease length.</param>
    public void Checkpoint(string? cursor, long bytes, long rowsAdded, DateTimeOffset now, TimeSpan lease)
    {
        var nowUtc = now.ToUniversalTime();
        CheckpointCursor = cursor;
        CheckpointBytes = bytes;
        RowCount += rowsAdded;
        LeaseUntilUtc = nowUtc + lease;
        Renew();
    }

    /// <summary>Marks the job Completed.</summary>
    /// <param name="sizeBytes">The size of the file.</param>
    /// <param name="sha256">The SHA-256 of the file.</param>
    /// <param name="now">The current time (UTC).</param>
    public void Complete(long sizeBytes, string sha256, DateTimeOffset now)
    {
        State = ExportState.Completed;
        FileSizeBytes = sizeBytes;
        Sha256 = sha256;
        LeaseOwner = null;
        LeaseUntilUtc = null;
        FinishedAtUtc = now.ToUniversalTime();
        Renew();
    }

    /// <summary>Marks the job Failed for good.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The message (cut to <see cref="MaxErrorLength"/>).</param>
    /// <param name="now">The current time (UTC).</param>
    public void Fail(string code, string message, DateTimeOffset now)
    {
        State = ExportState.Failed;
        ErrorCode = code;
        ErrorMessage = Cut(message);
        LeaseOwner = null;
        LeaseUntilUtc = null;
        FinishedAtUtc = now;
        Renew();
    }

    /// <summary>A failed attempt that may be tried again: the job goes back to the queue with its checkpoint, until the attempts run out.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="now">The current time (UTC).</param>
    public void Release(string message, DateTimeOffset now)
    {
        if (Attempts >= MaxAttempts)
        {
            Fail("Export.Failed", message, now);
            return;
        }

        State = ExportState.Pending;
        LeaseOwner = null;
        LeaseUntilUtc = null;
        ErrorMessage = Cut(message);
        Renew();
    }

    /// <summary>Cancels the job.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>Success, or <c>Export.AlreadyFinished</c> when the job is terminal.</returns>
    public ChronoAuditResult Cancel(DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Conflict("Export.AlreadyFinished", "This export has already finished and cannot be cancelled."));
        }

        State = ExportState.Cancelled;
        LeaseOwner = null;
        LeaseUntilUtc = null;
        FinishedAtUtc = now;
        Renew();
        return ChronoAuditResult.Success();
    }

    private static string Cut(string message) => message.Length > MaxErrorLength ? message[..MaxErrorLength] : message;

    private void Renew() => ConcurrencyStamp = Guid.NewGuid();
}

/// <summary>The caps of the exports.</summary>
public static class ExportLimits
{
    /// <summary>The most data rows an Excel sheet holds (its row limit minus the header row); more makes <c>Export.TooLarge</c>.</summary>
    public const long MaxXlsxRows = 1_048_575;

    /// <summary>The most jobs a list returns.</summary>
    public const int MaxListed = 50;

    /// <summary>The longest parameters JSON of a request, in characters.</summary>
    public const int MaxParametersLength = 16_000;
}
