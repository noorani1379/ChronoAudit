namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Who is making the change that is being audited. The host implements it once (typically from the authenticated user of the current request)
/// and registers it as a scoped service. Only <see cref="UserId"/> and <see cref="DisplayName"/> are required knowledge; every other member
/// has a neutral default so a small host can implement just those two.
/// </summary>
public interface IAuditActor
{
    /// <summary>The identifier of the signed-in user, or <see langword="null"/> when there is none (a background job, a seed).</summary>
    string? UserId { get; }

    /// <summary>A human-readable name for the stamps <c>CreatedByName</c>, <c>ModifiedByName</c> and <c>DeletedByName</c>.</summary>
    string? DisplayName { get; }

    /// <summary>The client address of the request, written to the audit metadata as <c>Ip</c>.</summary>
    string? UserIp => null;

    /// <summary>The session of the request, written to the audit metadata as <c>SessionId</c>.</summary>
    string? SessionId => null;

    /// <summary>The user's main role, used for entities marked with <see cref="AuditActorNameAsRoleAttribute"/> when <see cref="RoleTitle"/> is empty.</summary>
    string? Role => null;

    /// <summary>A display title of the role, preferred over <see cref="Role"/> for entities marked with <see cref="AuditActorNameAsRoleAttribute"/>.</summary>
    string? RoleTitle => null;

    /// <summary>
    /// The user who really performed the action when it is done on behalf of someone else (a delegate acting for another party); the audit row
    /// records this user as the actor. Defaults to <see cref="UserId"/>.
    /// </summary>
    string? ActorUserId => UserId;

    /// <summary>True when the action is done on behalf of another party; the display name is then formatted with <see cref="AuditTrailOptions.DelegatedDisplayNameFormat"/>.</summary>
    bool IsDelegated => false;

    /// <summary>
    /// Extra metadata the host wants on every automatic audit row of this request (for example the delegation details). It is written first,
    /// followed by <c>Ip</c> and <c>SessionId</c>. <see langword="null"/> or empty adds nothing.
    /// </summary>
    object? AuditMetadata => null;
}
