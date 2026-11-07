# Audit capture and change history

ChronoAudit records every change your `AuditableDbContext` saves, in the same transaction as the change, and lets you read that history back by table, by record, or by actor, resource and action.

## 1. What is captured

On every `SaveChanges` / `SaveChangesAsync` the context inspects the change tracker and writes one row to its `AuditLogEntries` table for each added, modified or deleted entity:

| Entity state | Audit action | What the row holds |
|---|---|---|
| Added | `Auto_Added` | the initial values |
| Modified | `Auto_Modified` | only the columns that changed, as old and new values; nothing is written when nothing changed |
| Deleted, entity implements `IAuditableEntity` | `Auto_SoftDeleted` | the entity becomes `IsDeleted = true`; a global query filter hides it |
| Deleted, any other entity | `Auto_Deleted` | a physical delete; the audit row keeps the last state of the row |

The constants are in `AuditActions` (`AuditActions.AutoAdded`, `AutoModified`, `AutoSoftDeleted`, `AutoDeleted`). Each row also carries the actor (`IAuditActor`), the correlation id (`ICorrelationContext`), the client IP and the session id when the actor knows them, and the time from the context's `TimeProvider` (always UTC, offset zero).

An entity whose key the **database generates** (an `int` identity column on SQL Server, a sequence on PostgreSQL) has no real key until the insert has run (and the same holds for a foreign key that points at such an entity, in a child that is added or changed in the same save), and an audit row cannot be corrected afterwards. So for such entities ChronoAudit saves the entity first and writes its `Auto_Added` row right after, with the real key and the final values, in the same transaction; the audit rows of everything else in the same save are written together with the entities as before. `SaveChangesAsync` then returns the number of rows including those late audit rows, and the tracker accepts the changes at that point even when you passed `acceptAllChangesOnSuccess: false`. If the audit rows of new entities cannot be written, the whole transaction is rolled back and a `ChronoAuditSaveException` is thrown (it is deliberately not retried by an EF execution strategy, because the tracker has already accepted the entities): discard the context and repeat the unit of work. Keys you assign yourself, such as a `Guid` created in code, need none of this.

Because the audit rows are saved by the same `SaveChanges` as the business change, either both exist or neither does. The context then wakes the hash worker, which adds the new rows to the HMAC chain a moment later (see [hash chain and anchors](hash-chain-and-anchors.md)). Until a row is hashed it is *pending*; reading and searching work on pending rows too.

### What you control

- A property marked `[SensitiveData]` is stored as `***MASKED***` in the old and the new values, and flagged as secret in the table catalog. Personal fields configured in `ChronoAudit:PersonalData` are masked when read, not when stored (see [personal data](personal-data.md)).
- An entity that implements `IExcludedFromAutoAudit` is not audited at all (use it for log tables that are the record themselves).
- An entity that implements `IAppendOnly` can only be inserted: the context rejects an update or a delete of it.
- `[AuditActorNameAsRole]` on an entity stores the actor's role title as `CreatedByName` instead of the person's name.
- `AuditTrailOptions` (`ChronoAudit:Audit`): `SystemUserId` and `SystemDisplayName` (written when nobody is signed in, default `System`), `DelegatedDisplayNameFormat` (default `{0} (on behalf)`, used when someone acts for another user), `BacklogWarnRows` (warn when a module has more unhashed rows than this, default 1000). Change the texts before the first row exists: they are written into rows.

## 2. Hand-written audit rows

For an action that is not an entity change (a login, a permission change, a viewed report) inject `IAuditLogWriter`. It stages the row; the next `SaveChangesAsync` of the module's context saves it in the same transaction, so the row exists if and only if the change it describes was committed.

`LibraryDbContext` is the context of the [getting started guide](getting-started.md).

```csharp
using ChronoAudit.EntityFrameworkCore;

public sealed class RoleService(LibraryDbContext db, IAuditLogWriter audit)
{
    public const string RoleGranted = "Library.RoleGranted";

    public async Task GrantAsync(string actorUserId, string memberId, string role, CancellationToken ct)
    {
        // ... change something in db ...

        await audit.AppendHashedAsync(actorUserId, RoleGranted, "Member", memberId, new { Role = role }, ct);
        await db.SaveChangesAsync(ct);
    }
}
```

`metadata` is serialized to JSON; when it is a JSON object the current actor's IP and session id are added to it. Use your own constants for `action`; names that start with `ChronoAudit.` are used by the library itself.

When the row is evidence that must not be released unless it was recorded (an exported document, a viewed file) use `IAuditRecorder` instead: it records the row first, in a unit of work of its own, and returns a failure when that did not succeed, so the caller can refuse to hand out the result.

## 3. Reading the history

### One table or one record: `IChangeReader`

`IChangeReader` is scoped. It returns field-level changes (what the old and the new value of each column were), oldest first, in pages of 1 to 200. It is for the **All** scope only: any other scope gets `Table.NotFound`, so a table's existence is not revealed to someone who may not read it.

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;

public sealed class HistoryService(IChangeReader changes)
{
    public async Task<IReadOnlyList<ChangeRow>> BookHistoryAsync(int bookId, string callerUserId, CancellationToken ct)
    {
        var scope = ChronoAuditScope.Everything(callerUserId);
        var rows = new List<ChangeRow>();
        string? cursor = null;

        do
        {
            var result = await changes.RecordAsync("library", "Books", bookId.ToString(), scope, cursor, 100, reveal: false, ct);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(result.Error.Code + ": " + result.Error.Message);
            }

            rows.AddRange(result.Value.Items);
            cursor = result.Value.NextCursor;
        }
        while (cursor is not null);

        return rows;
    }

    public async Task<ChangePage> RecentBookChangesAsync(string callerUserId, DateTimeOffset since, CancellationToken ct)
    {
        var result = await changes.TableAsync(
            "library", "Books", ChronoAuditScope.Everything(callerUserId), since, null, null, 50, reveal: false, ct);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);
    }
}
```

- `module` is the name you gave to `AddModule`; `table` is the table name; a composite key is written with its parts joined by `|`.
- A `ChangeRow` has `Sequence`, `OccurredAtUtc`, `Action`, `ActorUserId`, `ActorDisplayName`, `OnBehalfOfUserId`, `ResourceType`, `RecordKey`, `Fields` (a list of `FieldChange(Field, From, To)`) and `PayloadHash`.
- `ChangePage.PersonalDataRevealed` says whether personal fields in the page are whole. With `reveal: false` they are masked. With `reveal: true` the call is allowed only when your `IPersonalDataRevealPolicy` says yes, and a `ChronoAudit.PersonalDataRevealed` audit row is written first; if that row cannot be written, nothing is returned. See [personal data](personal-data.md).
- Errors are values: `Table.NotFound`, `Record.NotFound`, `ChronoAudit.InvalidCursor`, `ChronoAudit.Validation` (see [error codes](error-codes.md)).

### By actor, resource or action: `IAuditSearch`

`IAuditSearch` is a singleton. It returns raw audit rows, newest first, with keyset paging. The range is at most 31 days, or 366 days when you also give an actor or a resource.

```csharp
using ChronoAudit.EntityFrameworkCore;

public sealed class AuditReport(IAuditSearch search)
{
    public async Task<IReadOnlyList<AuditSearchRow>> LastDayOfUserAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var query = new AuditSearchQuery(Module: "library", From: now.AddDays(-1), To: now, ActorUserId: userId, Limit: 100);

        var result = await search.SearchAsync(query, ct);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Code);
        }

        return result.Value.Items;
    }
}
```

Filters: `ActorUserId`, `ResourceType`, `ResourceId`, `Action`; `Cursor` is the `NextCursor` of the previous page. An `AuditSearchRow` holds `Sequence`, `OccurredAtUtc`, `ActorUserId`, `Action`, `ResourceType`, `ResourceId`, `OldValues`, `NewValues`, `Metadata`, `PreviousHash` and `PayloadHash` (both null while the row is pending).

`IAuditSearch` does not check who is asking: it is for server-side code. The HTTP endpoint `GET /chronoaudit/audit/{module}/search` allows only callers with the All scope (see [HTTP endpoints](http-endpoints.md)).

### Soft-deleted rows

The global query filter hides soft-deleted entities from your own queries. Use `IgnoreQueryFilters()` to see them. The full history of a deleted record is still available through `IChangeReader`.

## 4. Over HTTP

With `MapChronoAudit` the same readers are routes (`/chronoaudit/history/...`, `/chronoaudit/audit/...`). Every route sits behind the authorization policy you named, and the history and audit routes also need the All access scope. See [HTTP endpoints](http-endpoints.md).
