namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Thrown by <see cref="AuditableDbContext"/> when the audit rows of newly added entities could not be written after the entities themselves
/// were saved (entities whose key the database generates have their row written in a second step, in the same transaction). The
/// transaction is rolled back, so nothing was stored. The tracker, however, already accepted the entities: discard the context and start
/// the unit of work again.
/// </summary>
public sealed class ChronoAuditSaveException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The failure of the second save.</param>
    public ChronoAuditSaveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
