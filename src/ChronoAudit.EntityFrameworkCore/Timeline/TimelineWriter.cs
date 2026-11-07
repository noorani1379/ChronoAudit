using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>A user who took part in the group of an event.</summary>
/// <param name="UserId">The user id (1 to 64 characters).</param>
/// <param name="Role">The role in the host's words, for example <c>initiator</c> (1 to 64 characters).</param>
public sealed record TimelineParticipantInput(string UserId, string Role);

/// <summary>An event the host records.</summary>
/// <param name="SourceEventId">The id of the event in the host; a second event with the same id is ignored.</param>
/// <param name="EventType">The event type (max 128).</param>
/// <param name="SubjectType">What the event is about (max 128).</param>
/// <param name="SubjectId">The subject identity (max 128).</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="ActorDisplayName">The name to show for the actor (max 200).</param>
/// <param name="Payload">The event data as a JSON text (max 32 000 characters).</param>
/// <param name="GroupKey">An optional key that groups events into one timeline (max 128).</param>
/// <param name="ActorUserId">The user who caused the event (max 64).</param>
/// <param name="ActorRole">The role of the actor (max 128).</param>
/// <param name="OnBehalfOfUserId">The user on whose behalf the actor acted (max 64).</param>
/// <param name="AccessGroupId">The access group for the Group scope (max 64).</param>
/// <param name="CorrelationId">The correlation id of the request.</param>
/// <param name="SchemaVersion">The payload format version.</param>
/// <param name="IsBackfilled">True for an event imported after the fact.</param>
/// <param name="Participants">The users who took part in the group; ignored without a group key.</param>
public sealed record TimelineEventInput(
    Guid SourceEventId,
    string EventType,
    string SubjectType,
    string SubjectId,
    DateTimeOffset OccurredAtUtc,
    string ActorDisplayName,
    string Payload,
    string? GroupKey = null,
    string? ActorUserId = null,
    string? ActorRole = null,
    string? OnBehalfOfUserId = null,
    string? AccessGroupId = null,
    Guid? CorrelationId = null,
    short SchemaVersion = 1,
    bool IsBackfilled = false,
    IReadOnlyList<TimelineParticipantInput>? Participants = null);

/// <summary>Records business events. Recording is idempotent by <see cref="TimelineEventInput.SourceEventId"/>.</summary>
public interface ITimelineWriter
{
    /// <summary>
    /// Stores the event (and the participants that are not stored yet) in a unit of work of its own. A redelivered event changes nothing.
    /// </summary>
    /// <param name="input">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Success (also for an event that was stored before), or <c>Timeline.InvalidEvent</c>.</returns>
    Task<ChronoAuditResult> RecordAsync(TimelineEventInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the event to the caller's context, to be saved in the caller's transaction. Only the event is staged: participants need a
    /// read of the table, so use <see cref="RecordAsync"/> when they matter. A second event with the same source id fails the caller's
    /// save with a <see cref="DbUpdateException"/>; a handler that may see the same message twice should prefer <see cref="RecordAsync"/>.
    /// </summary>
    /// <param name="storeContext">The context that holds the store tables.</param>
    /// <param name="input">The event.</param>
    /// <exception cref="ArgumentException">The event is not valid.</exception>
    void Stage(DbContext storeContext, TimelineEventInput input);
}

internal sealed class TimelineWriter(
    ChronoAuditStoreRegistration registration, IServiceScopeFactory scopeFactory, IChronoAuditCalendar calendar, TimeProvider clock) : ITimelineWriter
{
    internal const int MaxPayloadLength = 32_000;
    private const int MaxAttempts = 3;

    public async Task<ChronoAuditResult> RecordAsync(TimelineEventInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var built = Build(input);
        if (built.IsFailure)
        {
            return ChronoAuditResult.Failure(built.Error);
        }

        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        var dialect = ChronoAuditDialect.For(db);

        // A redelivered event changes nothing, not even its participants.
        if (await db.Set<TimelineEvent>().AsNoTracking().AnyAsync(e => e.SourceEventId == input.SourceEventId, cancellationToken))
        {
            return ChronoAuditResult.Success();
        }

        for (var attempt = 1; ; attempt++)
        {
            var timelineEvent = built.Value;
            db.Set<TimelineEvent>().Add(timelineEvent);
            foreach (var participant in await MissingParticipantsAsync(db, input, timelineEvent.RecordedAtUtc, cancellationToken))
            {
                db.Set<TimelineParticipant>().Add(participant);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ChronoAuditResult.Success();
            }
            catch (DbUpdateException ex) when (dialect.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();

                // Either the same event was recorded by a racing writer (success), or a racing writer inserted one of our participants:
                // look again and try once more with what is stored now.
                if (await db.Set<TimelineEvent>().AsNoTracking().AnyAsync(e => e.SourceEventId == input.SourceEventId, cancellationToken))
                {
                    return ChronoAuditResult.Success();
                }

                if (attempt >= MaxAttempts)
                {
                    throw;
                }

                built = Build(input);
            }
        }
    }

    public void Stage(DbContext storeContext, TimelineEventInput input)
    {
        ArgumentNullException.ThrowIfNull(storeContext);
        ArgumentNullException.ThrowIfNull(input);
        var built = Build(input);
        if (built.IsFailure)
        {
            throw new ArgumentException(built.Error.Message, nameof(input));
        }

        storeContext.Set<TimelineEvent>().Add(built.Value);
    }

    private static async Task<List<TimelineParticipant>> MissingParticipantsAsync(DbContext db, TimelineEventInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (input.GroupKey is not { } group || input.Participants is not { Count: > 0 } participants)
        {
            return [];
        }

        var wanted = participants.DistinctBy(p => (p.UserId, p.Role)).ToList();
        var users = wanted.Select(p => p.UserId).Distinct().ToList();

        // One statement for all participants; the role is compared in memory (the list is small).
        var existing = await db.Set<TimelineParticipant>().AsNoTracking()
            .Where(p => p.GroupKey == group && users.Contains(p.UserId))
            .Select(p => new { p.UserId, p.Role })
            .ToListAsync(cancellationToken);
        var have = existing.Select(p => (p.UserId, p.Role)).ToHashSet();
        return wanted
            .Where(p => !have.Contains((p.UserId, p.Role)))
            .Select(p => new TimelineParticipant { GroupKey = group, UserId = p.UserId, Role = p.Role, FirstSeenAtUtc = now })
            .ToList();
    }

    private ChronoAuditResult<TimelineEvent> Build(TimelineEventInput input)
    {
        static ChronoAuditError Invalid(string what) => ChronoAuditError.Validation("Timeline.InvalidEvent", what);

        if (input.SourceEventId == Guid.Empty)
        {
            return Invalid("The source event id must not be empty.");
        }

        var limits = new (string Name, string? Value, int Max, bool Required)[]
        {
            ("EventType", input.EventType, 128, true),
            ("SubjectType", input.SubjectType, 128, true),
            ("SubjectId", input.SubjectId, 128, true),
            ("GroupKey", input.GroupKey, 128, false),
            ("ActorUserId", input.ActorUserId, 64, false),
            ("ActorDisplayName", input.ActorDisplayName, 200, true),
            ("ActorRole", input.ActorRole, 128, false),
            ("OnBehalfOfUserId", input.OnBehalfOfUserId, 64, false),
            ("AccessGroupId", input.AccessGroupId, 64, false),
        };
        foreach (var (name, value, max, required) in limits)
        {
            if (value is null ? required : (required && string.IsNullOrWhiteSpace(value)) || value.Length > max)
            {
                return Invalid($"{name} is {(required ? "required and " : string.Empty)}limited to {max} characters.");
            }
        }

        if (input.Payload is null || input.Payload.Length > MaxPayloadLength)
        {
            return Invalid($"The payload is required and limited to {MaxPayloadLength} characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(input.Payload);
        }
        catch (JsonException)
        {
            return Invalid("The payload must be valid JSON.");
        }

        foreach (var participant in input.Participants ?? [])
        {
            if (string.IsNullOrWhiteSpace(participant.UserId) || participant.UserId.Length > 64 || string.IsNullOrWhiteSpace(participant.Role) || participant.Role.Length > 64)
            {
                return Invalid("A participant needs a user id and a role of 1 to 64 characters.");
            }
        }

        var occurred = input.OccurredAtUtc.ToUniversalTime();
        var parts = calendar.PartsOf(occurred);
        return new TimelineEvent
        {
            SourceEventId = input.SourceEventId,
            EventType = input.EventType,
            SchemaVersion = input.SchemaVersion,
            SubjectType = input.SubjectType,
            SubjectId = input.SubjectId,
            GroupKey = input.GroupKey,
            OccurredAtUtc = occurred,
            CalendarDate = (parts.Year * 10000) + (parts.Month * 100) + parts.Day,
            RecordedAtUtc = clock.GetUtcNow().ToUniversalTime(),
            ActorUserId = input.ActorUserId,
            ActorDisplayName = input.ActorDisplayName,
            ActorRole = input.ActorRole,
            OnBehalfOfUserId = input.OnBehalfOfUserId,
            AccessGroupId = input.AccessGroupId,
            CorrelationId = input.CorrelationId,
            IsBackfilled = input.IsBackfilled,
            Payload = input.Payload,
        };
    }
}
