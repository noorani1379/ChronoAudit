namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// One business event of the timeline: something that happened to a subject, recorded by the host (a state change, a payment, a
/// decision). Evidence: every column is set at insert and never changes; the table is guarded against update and delete. The
/// <see cref="SourceEventId"/> is unique, so a redelivered event stores one row. The payload is stored as the host sent it; it is masked
/// only when read.
/// </summary>
public sealed class TimelineEvent : IAppendOnly, IExcludedFromAutoAudit
{
    /// <summary>The row id (identity); timelines page by it after the time.</summary>
    public long Id { get; init; }

    /// <summary>The id of the event in the host (an outbox message id, a domain event id); unique, for idempotency.</summary>
    public required Guid SourceEventId { get; init; }

    /// <summary>The event type, for example <c>order.paid</c> (max 128).</summary>
    public required string EventType { get; init; }

    /// <summary>The version of the payload format.</summary>
    public short SchemaVersion { get; init; } = 1;

    /// <summary>What the event is about, for example <c>Order</c> (max 128).</summary>
    public required string SubjectType { get; init; }

    /// <summary>The identity of the subject (max 128).</summary>
    public required string SubjectId { get; init; }

    /// <summary>An optional key that groups the events of several subjects into one timeline, for example a case or a process (max 128).</summary>
    public string? GroupKey { get; init; }

    /// <summary>When it happened (UTC, offset zero).</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>The day of <see cref="OccurredAtUtc"/> in the configured calendar, as <c>year*10000 + month*100 + day</c>.</summary>
    public int CalendarDate { get; init; }

    /// <summary>When ChronoAudit stored it (UTC).</summary>
    public required DateTimeOffset RecordedAtUtc { get; init; }

    /// <summary>The user who caused it, when there was one (max 64).</summary>
    public string? ActorUserId { get; init; }

    /// <summary>The name to show for the actor, kept so the timeline reads the same later (max 200).</summary>
    public required string ActorDisplayName { get; init; }

    /// <summary>The role the actor had (max 128).</summary>
    public string? ActorRole { get; init; }

    /// <summary>The user on whose behalf the actor acted (max 64).</summary>
    public string? OnBehalfOfUserId { get; init; }

    /// <summary>The access group of the event, used by the Group scope (max 64).</summary>
    public string? AccessGroupId { get; init; }

    /// <summary>Ties the event to the audit rows and the API calls of the same request.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>True when the event was imported after the fact rather than recorded when it happened.</summary>
    public bool IsBackfilled { get; init; }

    /// <summary>The event data as JSON.</summary>
    public required string Payload { get; init; }
}

/// <summary>
/// A user who took part in a group of events. It answers "which groups did this user take part in" for the Own scope. Rows are only
/// inserted when absent and never changed.
/// </summary>
public sealed class TimelineParticipant : IExcludedFromAutoAudit
{
    /// <summary>The group key of the events (max 128).</summary>
    public required string GroupKey { get; init; }

    /// <summary>The participating user (max 64).</summary>
    public required string UserId { get; init; }

    /// <summary>The role of the user in the group, in the host's words (max 64).</summary>
    public required string Role { get; init; }

    /// <summary>When the participation was first seen (UTC).</summary>
    public required DateTimeOffset FirstSeenAtUtc { get; init; }
}
