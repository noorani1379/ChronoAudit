namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// An entity whose lifecycle <see cref="AuditableDbContext"/> stamps: who and when created, modified and deleted, plus a soft-delete flag.
/// A delete of such an entity becomes <c>IsDeleted = true</c>, and a global query filter hides deleted rows. Implement it directly or derive
/// from <see cref="AuditableEntityBase"/>. The context sets every member; application code only reads them.
/// </summary>
public interface IAuditableEntity
{
    /// <summary>The user id that created the row.</summary>
    string? CreatedBy { get; set; }

    /// <summary>The user id that last modified the row.</summary>
    string? ModifiedBy { get; set; }

    /// <summary>The user id that deleted the row.</summary>
    string? DeletedBy { get; set; }

    /// <summary>The display name of the creator.</summary>
    string? CreatedByName { get; set; }

    /// <summary>The display name of the last modifier.</summary>
    string? ModifiedByName { get; set; }

    /// <summary>The display name of the deleter.</summary>
    string? DeletedByName { get; set; }

    /// <summary>When the row was created (UTC).</summary>
    DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the row was last modified (UTC).</summary>
    DateTimeOffset? ModifiedAtUtc { get; set; }

    /// <summary>When the row was deleted (UTC).</summary>
    DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>True once the row was soft deleted.</summary>
    bool IsDeleted { get; set; }
}

/// <summary>The ten audit members of <see cref="IAuditableEntity"/> as plain properties, for entities that need no other base class.</summary>
public abstract class AuditableEntityBase : IAuditableEntity
{
    /// <inheritdoc />
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public string? ModifiedBy { get; set; }

    /// <inheritdoc />
    public string? DeletedBy { get; set; }

    /// <inheritdoc />
    public string? CreatedByName { get; set; }

    /// <inheritdoc />
    public string? ModifiedByName { get; set; }

    /// <inheritdoc />
    public string? DeletedByName { get; set; }

    /// <inheritdoc />
    /// <remarks>Set by <see cref="AuditableDbContext"/> from its <see cref="TimeProvider"/> when the row is added.</remarks>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? ModifiedAtUtc { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <inheritdoc />
    public bool IsDeleted { get; set; }
}
