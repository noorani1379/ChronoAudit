namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Appends a hand-written audit row (an action that is not a plain entity change: a login, a permission change, a viewed report).</summary>
public interface IAuditLogWriter
{
    /// <summary>
    /// Stages one audit row. It is saved, in the same transaction, by the next <c>SaveChangesAsync</c> of the module's
    /// <see cref="AuditableDbContext"/>, so the row exists if and only if the business change it describes was committed.
    /// </summary>
    /// <param name="actorUserId">Who did it.</param>
    /// <param name="action">What was done (a constant of the host, for example a member of its own actions class).</param>
    /// <param name="resourceType">The kind of thing it was done to (usually the table name).</param>
    /// <param name="resourceId">The identity of that thing.</param>
    /// <param name="metadata">Optional details, serialized to JSON. When it is a JSON object, <c>Ip</c> and <c>SessionId</c> of the current actor are added to it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the row is staged.</returns>
    Task AppendHashedAsync(
        string actorUserId,
        string action,
        string resourceType,
        string resourceId,
        object? metadata = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The scoped store of staged audit rows that <see cref="AuditableDbContext"/> drains when it saves.</summary>
public interface IAuditLogAccessor
{
    /// <summary>The rows staged so far in this scope.</summary>
    /// <returns>The staged rows.</returns>
    IReadOnlyCollection<AuditLogEntry> GetPendingLogs();

    /// <summary>Forgets the staged rows (called after they were saved).</summary>
    void Clear();
}
