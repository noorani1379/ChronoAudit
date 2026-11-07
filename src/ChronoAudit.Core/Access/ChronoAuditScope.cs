namespace ChronoAudit;

/// <summary>The data scope of one request, applied inside every query (never after it).</summary>
/// <param name="Kind">How wide the scope is.</param>
/// <param name="UserId">The caller; null only for a system caller with the <see cref="ScopeKind.All"/> scope.</param>
/// <param name="GroupId">The caller's group; set for <see cref="ScopeKind.Group"/>.</param>
public sealed record ChronoAuditScope(ScopeKind Kind, string? UserId, string? GroupId)
{
    /// <summary>A scope that sees only the items of <paramref name="userId"/>.</summary>
    /// <param name="userId">The caller.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is null or empty.</exception>
    public static ChronoAuditScope OwnOf(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new ChronoAuditScope(ScopeKind.Own, userId, null);
    }

    /// <summary>A scope that sees the items of <paramref name="userId"/> and of <paramref name="groupId"/>.</summary>
    /// <param name="userId">The caller.</param>
    /// <param name="groupId">The caller's group.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentException">An id is null or empty.</exception>
    public static ChronoAuditScope GroupOf(string userId, string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return new ChronoAuditScope(ScopeKind.Group, userId, groupId);
    }

    /// <summary>A scope that sees everything.</summary>
    /// <param name="userId">The caller, or null for a system caller.</param>
    /// <returns>The scope.</returns>
    public static ChronoAuditScope Everything(string? userId) => new(ScopeKind.All, userId, null);

    /// <summary>Tells whether an item with the given owner and group is visible in this scope.</summary>
    /// <param name="ownerUserId">The owner of the item.</param>
    /// <param name="ownerGroupId">The group of the item.</param>
    /// <returns>True when the item is visible.</returns>
    public bool Sees(string? ownerUserId, string? ownerGroupId) => Kind switch
    {
        ScopeKind.All => true,
        ScopeKind.Group => IsSameUser(ownerUserId) || (GroupId is not null && string.Equals(GroupId, ownerGroupId, StringComparison.Ordinal)),
        ScopeKind.Own => IsSameUser(ownerUserId),
        _ => false,
    };

    private bool IsSameUser(string? ownerUserId) =>
        UserId is not null && string.Equals(UserId, ownerUserId, StringComparison.Ordinal);
}
