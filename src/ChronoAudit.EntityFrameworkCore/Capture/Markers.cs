namespace ChronoAudit.EntityFrameworkCore;

/// <summary>On a property: its value is replaced by <c>***MASKED***</c> in every audit row (old and new values), and the column is flagged as secret in the table catalog.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveDataAttribute : Attribute
{
}

/// <summary>
/// On an entity: the name stored as <c>CreatedByName</c> is the actor's role title (when there is one) instead of the person's display name,
/// so a payment record shows "cashier" and not who the cashier is.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AuditActorNameAsRoleAttribute : Attribute
{
}

/// <summary>
/// Marks an entity whose rows may only be inserted (history, timeline and evidence records).
/// <c>AuditableDbContext.SaveChangesAsync</c> rejects Modified and Deleted states for it. Do not use bulk update or delete on its set. A wrong record is
/// corrected with a compensating record, never by editing the original.
/// </summary>
public interface IAppendOnly
{
}

/// <summary>
/// Opt-out from the automatic technical audit (<c>Auto_Added</c> / <c>Auto_Modified</c> rows).
/// For high-volume evidence or projection tables that are themselves the record (for example API request
/// logs), where an audit row per insert would only double the write volume.
/// </summary>
public interface IExcludedFromAutoAudit
{
}

/// <summary>Supplies the identifier that ties every audit row of one request or job together (usually the request's correlation id).</summary>
public interface ICorrelationContext
{
    /// <summary>The id of the current request or job.</summary>
    Guid CorrelationId { get; }
}
