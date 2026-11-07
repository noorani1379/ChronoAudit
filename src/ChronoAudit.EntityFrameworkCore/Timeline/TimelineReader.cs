using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>One event of a timeline as a reader sees it (the payload is masked).</summary>
/// <param name="Id">The row id.</param>
/// <param name="SourceEventId">The id of the event in the host.</param>
/// <param name="EventType">The event type.</param>
/// <param name="Title">The title to show: from the host's <see cref="ITimelineTitleProvider"/> when there is one, else the event type.</param>
/// <param name="SubjectType">What the event is about.</param>
/// <param name="SubjectId">The subject identity.</param>
/// <param name="GroupKey">The group key, if any.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="ActorDisplayName">The name of the actor.</param>
/// <param name="ActorRole">The role of the actor.</param>
/// <param name="OnBehalfOfUserId">The user on whose behalf the actor acted.</param>
/// <param name="Payload">The event data as JSON, with secrets and personal fields masked.</param>
public sealed record TimelineItem(
    long Id, Guid SourceEventId, string EventType, string Title, string SubjectType, string SubjectId, string? GroupKey,
    DateTimeOffset OccurredAtUtc, string ActorDisplayName, string? ActorRole, string? OnBehalfOfUserId, string Payload);

/// <summary>A page of a timeline, newest first.</summary>
/// <param name="Items">The events.</param>
/// <param name="NextCursor">Pass it as <see cref="TimelineFilter.Before"/> to get the next (older) page; null on the last page.</param>
public sealed record TimelinePage(IReadOnlyList<TimelineItem> Items, string? NextCursor);

/// <summary>What to show of a timeline.</summary>
/// <param name="EventTypes">Only these event types (at most 20, each 1 to 128 characters).</param>
/// <param name="From">Only events at or after this time.</param>
/// <param name="To">Only events at or before this time.</param>
/// <param name="Before">The cursor of the previous page.</param>
/// <param name="Limit">The page size, 1 to 200.</param>
public sealed record TimelineFilter(IReadOnlyList<string>? EventTypes = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Before = null, int Limit = 50);

/// <summary>Gives the host's title of an event (for example a localized sentence). Optional; register it in the container.</summary>
public interface ITimelineTitleProvider
{
    /// <summary>The title of an event.</summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="schemaVersion">The payload format version.</param>
    /// <param name="payload">The stored payload JSON (not masked: do not copy personal data into the title).</param>
    /// <returns>The title, or null to use the event type.</returns>
    string? TitleOf(string eventType, int schemaVersion, string payload);
}

/// <summary>Reads timelines. Every timeline is newest first and the caller's scope is applied inside the query.</summary>
public interface ITimelineReader
{
    /// <summary>The events of one subject.</summary>
    /// <param name="subjectType">The subject type.</param>
    /// <param name="subjectId">The subject identity.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A page, or <c>ChronoAudit.Validation</c> / <c>ChronoAudit.InvalidCursor</c>.</returns>
    Task<ChronoAuditResult<TimelinePage>> OfSubjectAsync(string subjectType, string subjectId, TimelineFilter filter, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>The events of one group key.</summary>
    /// <param name="groupKey">The group key.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A page, or <c>ChronoAudit.Validation</c> / <c>ChronoAudit.InvalidCursor</c>.</returns>
    Task<ChronoAuditResult<TimelinePage>> OfGroupAsync(string groupKey, TimelineFilter filter, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>The events of one user: where the user is the actor, the on-behalf-of user, or a participant of the event's group.</summary>
    /// <param name="actorUserId">The user.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A page, or <c>ChronoAudit.Validation</c> / <c>ChronoAudit.InvalidCursor</c>.</returns>
    Task<ChronoAuditResult<TimelinePage>> OfActorAsync(string actorUserId, TimelineFilter filter, ChronoAuditScope scope, CancellationToken cancellationToken);
}

/// <summary>The position after which the next page starts: the time and the id of the last event of the previous page.</summary>
internal sealed record TimelineCursor(long OccurredAtTicks, long Id)
{
    private const string Prefix = "t1:";

    public string Encode() => CursorCodec.Encode(Prefix, OccurredAtTicks.ToString(CultureInfo.InvariantCulture) + ":" + Id.ToString(CultureInfo.InvariantCulture));

    public static TimelineCursor? Decode(string? text)
    {
        if (text is null || !CursorCodec.TryDecode(text, Prefix, out var plain))
        {
            return null;
        }

        var parts = plain.Split(':');
        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
               && ticks <= DateTimeOffset.MaxValue.UtcTicks
            ? new TimelineCursor(ticks, id)
            : null;
    }
}

/// <summary>What a caller of a scope may see of the events; used by the readers, the analytics subject, the exports and the evidence.</summary>
internal static class TimelineScope
{
    public static IQueryable<TimelineEvent> Visible(DbContext db, IQueryable<TimelineEvent> events, ChronoAuditScope scope)
    {
        var user = scope.UserId;
        var group = scope.GroupId;
        switch (scope.Kind)
        {
            case ScopeKind.All:
                return events;

            case ScopeKind.Group:
                // A comparison with a null id would match the events that have none: leave the term out instead.
                if (group is not null && user is not null)
                {
                    return events.Where(e => e.AccessGroupId == group || e.ActorUserId == user);
                }

                if (group is not null)
                {
                    return events.Where(e => e.AccessGroupId == group);
                }

                return user is not null ? events.Where(e => e.ActorUserId == user) : events.Where(_ => false);

            default:
                if (user is null)
                {
                    return events.Where(_ => false);
                }

                var participants = db.Set<TimelineParticipant>();
                return events.Where(e =>
                    e.ActorUserId == user || e.OnBehalfOfUserId == user || (e.GroupKey != null && participants.Any(p => p.UserId == user && p.GroupKey == e.GroupKey)));
        }
    }
}

internal sealed class TimelineReader(
    ChronoAuditStoreRegistration registration, IServiceScopeFactory scopeFactory, SensitiveDataMasker masker, PersonalDataMasking personalData, IServiceProvider services) : ITimelineReader
{
    private const int MaxLimit = 200;
    private const int MaxEventTypes = 20;

    public Task<ChronoAuditResult<TimelinePage>> OfSubjectAsync(string subjectType, string subjectId, TimelineFilter filter, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subjectType) || string.IsNullOrWhiteSpace(subjectId))
        {
            return Task.FromResult<ChronoAuditResult<TimelinePage>>(ChronoAuditError.Validation("ChronoAudit.Validation", "The subject type and id are required."));
        }

        return ReadAsync(filter, scope, (events, _) => events.Where(e => e.SubjectType == subjectType && e.SubjectId == subjectId), cancellationToken);
    }

    public Task<ChronoAuditResult<TimelinePage>> OfGroupAsync(string groupKey, TimelineFilter filter, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return Task.FromResult<ChronoAuditResult<TimelinePage>>(ChronoAuditError.Validation("ChronoAudit.Validation", "The group key is required."));
        }

        return ReadAsync(filter, scope, (events, _) => events.Where(e => e.GroupKey == groupKey), cancellationToken);
    }

    public Task<ChronoAuditResult<TimelinePage>> OfActorAsync(string actorUserId, TimelineFilter filter, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            return Task.FromResult<ChronoAuditResult<TimelinePage>>(ChronoAuditError.Validation("ChronoAudit.Validation", "The user id is required."));
        }

        return ReadAsync(
            filter,
            scope,
            (events, db) =>
            {
                var participants = db.Set<TimelineParticipant>();
                return events.Where(e =>
                    e.ActorUserId == actorUserId || e.OnBehalfOfUserId == actorUserId || (e.GroupKey != null && participants.Any(p => p.UserId == actorUserId && p.GroupKey == e.GroupKey)));
            },
            cancellationToken);
    }

    private async Task<ChronoAuditResult<TimelinePage>> ReadAsync(
        TimelineFilter filter, ChronoAuditScope scope, Func<IQueryable<TimelineEvent>, DbContext, IQueryable<TimelineEvent>> select, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(scope);
        var invalid = Validate(filter, out var cursor);
        if (invalid is not null)
        {
            return invalid;
        }

        using var serviceScope = scopeFactory.CreateScope();
        var db = (DbContext)serviceScope.ServiceProvider.GetRequiredService(registration.ContextType);
        var query = select(TimelineScope.Visible(db, db.Set<TimelineEvent>().AsNoTracking(), scope), db);

        if (filter.EventTypes is { Count: > 0 } types)
        {
            var wanted = types.ToArray();
            query = query.Where(e => wanted.Contains(e.EventType));
        }

        if (filter.From is { } from)
        {
            var at = from.ToUniversalTime();
            query = query.Where(e => e.OccurredAtUtc >= at);
        }

        if (filter.To is { } to)
        {
            var at = to.ToUniversalTime();
            query = query.Where(e => e.OccurredAtUtc <= at);
        }

        if (cursor is not null)
        {
            var at = new DateTimeOffset(cursor.OccurredAtTicks, TimeSpan.Zero);
            var id = cursor.Id;
            query = query.Where(e => e.OccurredAtUtc < at || (e.OccurredAtUtc == at && e.Id < id));
        }

        var rows = await query
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Take(filter.Limit + 1)
            .Select(e => new
            {
                e.Id, e.SourceEventId, e.EventType, e.SchemaVersion, e.SubjectType, e.SubjectId, e.GroupKey, e.OccurredAtUtc, e.ActorDisplayName, e.ActorRole, e.OnBehalfOfUserId, e.Payload,
            })
            .ToListAsync(cancellationToken);

        var more = rows.Count > filter.Limit;
        var page = rows.Take(filter.Limit).ToList();
        var titles = services.GetService<ITimelineTitleProvider>();
        var items = page.Select(e => new TimelineItem(
            e.Id, e.SourceEventId, e.EventType, titles?.TitleOf(e.EventType, e.SchemaVersion, e.Payload) ?? e.EventType, e.SubjectType, e.SubjectId, e.GroupKey,
            e.OccurredAtUtc, e.ActorDisplayName, e.ActorRole, e.OnBehalfOfUserId, MaskPayload(e.Payload))).ToList();
        return new TimelinePage(items, more ? new TimelineCursor(page[^1].OccurredAtUtc.UtcTicks, page[^1].Id).Encode() : null);
    }

    // Secrets first (the masker's names), then the configured personal fields; the stored payload is never changed.
    internal string MaskPayload(string payload) => personalData.MaskJson(masker.MaskJson(payload, codeIsSecret: false), reveal: false);

    private static ChronoAuditError? Validate(TimelineFilter filter, out TimelineCursor? cursor)
    {
        cursor = null;
        if (filter.Limit < 1 || filter.Limit > MaxLimit)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"Limit must be between 1 and {MaxLimit}.");
        }

        if (filter.EventTypes is { } types && (types.Count > MaxEventTypes || types.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128)))
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"At most {MaxEventTypes} event types, each 1 to 128 characters.");
        }

        if (filter.From is { } from && filter.To is { } to && from > to)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The start of the range must not be after its end.");
        }

        if (filter.Before is not null)
        {
            cursor = TimelineCursor.Decode(filter.Before);
            if (cursor is null)
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }
        }

        return null;
    }
}
