namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// One row of the audit table: who did what to which thing, when, with the values before and after, chained to the previous row by a hash.
/// Rows are append-only; the only fields that are ever written afterwards are the hash columns, by the hash worker.
/// </summary>
public sealed class AuditLogEntry
{
    /// <summary>The row's identity (a unique alternate key; the primary key is <see cref="SequenceNumber"/>).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The order of the rows. The database increments it, so concurrent writers still get one total order.</summary>
    public long SequenceNumber { get; private set; }

    /// <summary>Ties the row to the request or job that caused it.</summary>
    public Guid CorrelationId { get; init; }

    /// <summary>Who did it.</summary>
    public string ActorUserId { get; init; } = "System";

    /// <summary>What was done (<c>Auto_Added</c>, <c>Auto_Modified</c>, <c>Auto_SoftDeleted</c>, <c>Auto_Deleted</c>, or a host action).</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>The kind of thing it was done to: the table name for automatic rows.</summary>
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>The identity of that thing: for automatic rows the JSON of its key, for example <c>{"Id":"..."}</c>.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>The changed columns before the change, as JSON (secrets masked); null for an insert.</summary>
    public string? OldValues { get; init; }

    /// <summary>The changed columns after the change, as JSON (secrets masked); null for a physical delete.</summary>
    public string? NewValues { get; init; }

    /// <summary>Extra details of the row as JSON (the client address, the session, and whatever the host adds).</summary>
    public string? Metadata { get; init; }

    /// <summary>When it happened (UTC); set by the saving context from its <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>
    /// The day of <see cref="OccurredAtUtc"/> in the configured calendar and time zone as <c>year*10000 + month*100 + day</c>.
    /// Analytics group by it. It is not part of the row hash.
    /// </summary>
    public int CalendarDate { get; private set; }

    /// <summary>The hash of the row before this one in the chain; null for the first row.</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>The hash of this row. Empty until the background hash worker has processed it.</summary>
    public string? PayloadHash { get; private set; }

    /// <summary>The position of the row in the hash chain (1, 2, 3, ...); set once together with the hashes.</summary>
    public long? ChainIndex { get; private set; }

    /// <summary>Fills the hash columns. Called by the hash worker only; a row that already has a hash can never be changed.</summary>
    /// <param name="chainIndex">The position of the row in the chain.</param>
    /// <param name="previousHash">The hash of the previous row.</param>
    /// <param name="payloadHash">The hash of this row.</param>
    /// <exception cref="InvalidOperationException">The row is already hashed.</exception>
    public void SetHash(long chainIndex, string? previousHash, string payloadHash)
    {
        if (!string.IsNullOrEmpty(PayloadHash))
        {
            throw new InvalidOperationException("This audit log has already been hashed and cannot be altered.");
        }

        ChainIndex = chainIndex;
        PreviousHash = previousHash;
        PayloadHash = payloadHash;
    }

    internal void StampTime(DateTimeOffset occurredAtUtc, int calendarDate)
    {
        OccurredAtUtc = occurredAtUtc;
        CalendarDate = calendarDate;
    }
}
