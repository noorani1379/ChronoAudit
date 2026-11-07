# Timeline, data lag, consistency and evidence

The audit trail records *what a row looked like*. The timeline records *what happened in the business*: "order placed", "payment verified", "book returned". Your code records those events; ChronoAudit keeps them append-only, lets people read them by subject, case or person, and can issue an evidence bundle that ties a record's events to its audit rows and chain checks.

Needs `UseStore<T>()` and `AddTimeline()`. Add `AddDataLag()` for the data-lag header and `AddConsistencyWorker()` for the consistency check ([getting started](getting-started.md)).

## 1. Recording events

`ITimelineWriter.RecordAsync` stores one event in a unit of work of its own and is **idempotent by `SourceEventId`**: a second event with the same id changes nothing and still returns success, so an outbox handler or a message consumer can safely run twice.

```csharp
using System.Text.Json;
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;

public sealed class LoanService(ITimelineWriter timeline, TimeProvider clock)
{
    public async Task RecordReturnAsync(int bookId, int memberId, string librarianId, CancellationToken ct)
    {
        var recorded = await timeline.RecordAsync(
            new TimelineEventInput(
                SourceEventId: Guid.NewGuid(),
                EventType: "loan.returned",
                SubjectType: "Book",
                SubjectId: bookId.ToString(),
                OccurredAtUtc: clock.GetUtcNow(),
                ActorDisplayName: "Front desk",
                Payload: JsonSerializer.Serialize(new { bookId, memberId }),
                GroupKey: "member-" + memberId,
                ActorUserId: librarianId,
                Participants: [new TimelineParticipantInput(librarianId, "librarian"), new TimelineParticipantInput(memberId.ToString(), "member")]),
            ct);

        if (recorded.IsFailure)
        {
            throw new InvalidOperationException(recorded.Error.Code + ": " + recorded.Error.Message);
        }
    }
}
```

| Field | Meaning |
|---|---|
| `SourceEventId` | the id of the event in *your* system; the idempotency key |
| `EventType`, `SubjectType`, `SubjectId` | what happened, to what (each up to 128 characters, required) |
| `OccurredAtUtc` | when it happened (stored in UTC; the calendar day is computed in the configured calendar) |
| `ActorDisplayName` (200), `ActorUserId` (64), `ActorRole` (128), `OnBehalfOfUserId` (64) | who did it, and for whom |
| `Payload` | JSON text, up to 32,000 characters; **masked when read** (secrets and personal fields) but stored as sent |
| `GroupKey` | optional: events with the same key form one timeline (a case, a dossier, a customer) |
| `Participants` | the people who took part in the group, each with a role; only with a `GroupKey`. A user who took part in a group sees all its events (Own scope) |
| `AccessGroupId` | the group whose members may see the event (Group scope) |
| `CorrelationId`, `SchemaVersion`, `IsBackfilled` | the request id, the payload format version, and `true` for an event imported after the fact |

A bad event is `Timeline.InvalidEvent`; nothing is stored. Corrections are new events, never edits: the events table is append-only (the guard triggers refuse an update or a delete).

To record an event **in the same transaction** as your own change (the transactional outbox pattern), call `ITimelineWriter.Stage(storeContext, input)` before your `SaveChanges`. A staged duplicate fails that save, and participants are not staged, so prefer `RecordAsync` when either matters.

### Titles

An event shows its `eventType` as its title unless you register an `ITimelineTitleProvider` (`TitleOf(eventType, schemaVersion, payload)`, for example a translated sentence). The payload given to it is *not* masked, so do not copy personal data into the title.

## 2. Reading

Three views, all newest first, 1 to 200 per page, at most 20 `eventTypes` in a filter, with `from`, `to` and the `before` cursor: by subject, by group (`GroupKey`) and by actor. Over HTTP they are `GET /chronoaudit/timeline/subjects/{type}/{id}`, `groups/{key}` and `actors/{userId}` ([HTTP endpoints](http-endpoints.md)); in code they are `ITimelineReader.OfSubjectAsync`, `OfGroupAsync` and `OfActorAsync`.

What a caller sees depends on the scope: **All** every event; **Group** the events whose `AccessGroupId` is their group (and, with a user id, the events they caused); **Own** the events they caused, the ones done on their behalf, and every event of a group they are a participant of. The actor view of someone else shows only what the caller could see anyway.

## 3. Data lag

If your events arrive through an outbox, a read model is briefly behind the truth. Tell ChronoAudit how old the oldest undelivered item is and every successful read response carries `X-Data-Lag-Seconds`, so a screen can say "data as of 12 s ago".

```csharp
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class OutboxMessage
{
    public long Id { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }
}

public sealed class OutboxLagSource(LibraryDbContext db) : IChronoAuditDataLagSource
{
    public async Task<DateTimeOffset?> OldestWaitingAtUtcAsync(CancellationToken cancellationToken)
    {
        // The creation time of the oldest message that has not been turned into a timeline event yet; null when nothing waits.
        return await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.ProcessedAtUtc == null)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => (DateTimeOffset?)m.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public static class LagRegistration
{
    public static void Register(IServiceCollection services)
        => services.AddSingleton<IChronoAuditDataLagSource, OutboxLagSource>();
}
```

`AddDataLag()` reads every registered source and remembers the answer for `ChronoAudit:DataLag:CacheSeconds` (default 3), so a busy API does not query your outbox on every call. The header is sent only on a successful (2xx) answer from a read endpoint and only when a lag is known: no source, or every source failing, means no header. A failing source never breaks the call. The lag is never negative; `0` means nothing is waiting. Expose the header to browsers through CORS ([HTTP endpoints](http-endpoints.md)).


## 4. Consistency checks

The timeline and your own system should agree. A consistency check compares a number on each side; the worker runs every check every `ChronoAudit:Consistency:IntervalHours` (default 6, 1 to 168). A check that differs is run again after `GraceSeconds` (default 60, 0 to 3600), so a difference that is only a delivery in flight heals itself and is not reported.

```csharp
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class BookEventsCheck(LibraryDbContext db) : IConsistencyCheck
{
    public string Name => "books-vs-timeline";

    public async Task<IReadOnlyList<ConsistencyResult>> RunAsync(CancellationToken cancellationToken)
    {
        var expected = await db.Set<Book>().LongCountAsync(cancellationToken);
        var actual = await db.Set<TimelineEvent>().LongCountAsync(e => e.EventType == "book.created", cancellationToken);

        return [new ConsistencyResult(Name, expected, actual)];
    }
}

public static class CheckRegistration
{
    public static void Register(IServiceCollection services)
        => services.AddSingleton<IConsistencyCheck, BookEventsCheck>();
}
```

`Drift` is *actual minus expected*: negative means rows are missing in ChronoAudit, positive means orphans. A check that still drifts after the grace wait is logged as a warning (with both numbers) and counted in the gauge `chronoaudit.consistency.drifting_checks` of the `ChronoAudit` meter; alert on that gauge. A check that throws is logged as an error and the others still run. The worker never repairs anything.

## 5. Evidence bundles

`GET /chronoaudit/timeline/subjects/{subjectType}/{subjectId}/evidence` (or `IEvidenceService.BuildAsync`) returns, for one record, everything ChronoAudit knows in one JSON document: the timeline events, the audit rows of every module for that resource, the verification of each module's hash chain over those rows, and a SHA-256 of the events (`dataSha256`) that changes when any event or their order changes. Keep the bundle with a case file to show the history was intact when it was issued.

- Only the **All** scope may ask; anyone else gets `Evidence.NotFound`, the same answer as for a record that does not exist.
- The issue is recorded in the audit table (`ChronoAudit.EvidenceIssued`) *before* the bundle is returned. If it cannot be recorded, nothing is returned (`ChronoAudit.AuditUnavailable`).
- At most 1000 events or audit rows per bundle; more is `Evidence.TooLarge`.
- Payloads and values are masked like everywhere else.

```json
{
  "subjectType": "Orders",
  "subjectId": "d9118a64-735a-4625-9f51-3f96fc7ec27a",
  "dataSha256": "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
  "timeline": [],
  "auditRows": [ { "sequence": 1, "action": "Auto_Added", "resourceType": "Orders", "payloadHash": "2A1D..." } ],
  "chainChecks": [ { "module": "shop", "verification": { "isValid": true, "verifiedCount": 1, "pendingCount": 8, "failedAtChainIndex": null, "failedAtSequence": null, "failure": null, "nextAfterChainIndex": 1 } } ],
  "generatedAtUtc": "2026-11-01T08:00:02+00:00"
}
```

(`auditRows` is shortened here; each row has the same fields as an [audit search](audit-and-history.md) row.) `dataSha256` above is the hash of an empty timeline.
