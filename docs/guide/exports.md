# Exports

An export is a file (CSV or XLSX) built in the background from a *producer*: the activity log, the audit rows, an analytics result, the timeline, or something of your own. The caller asks, a worker produces the file in batches, and the caller downloads it when it is `Completed`. Nothing large is built inside a request.

Needs `UseStore<T>()`, `AddExports()` and `AddExportWorker()` ([getting started](getting-started.md)). Only run `AddExportWorker()` on the nodes that should produce files.

## 1. The life of a job

1. **Request.** `POST /chronoaudit/exports` (or `IExportService.RequestAsync`) with a producer key, a format and parameters. The producer validates the parameters and the caller's right to ask *now*; a bad request creates no job. A user may have at most `MaxActivePerUser` (default 3) `Pending` or `Running` jobs: more is `Export.TooManyActive`. The job is `Pending`.
2. **Production.** The worker takes the oldest claimable job under a **lease** (`LeaseSeconds`, default 120). It reads one batch (`BatchSize` rows, default 1000) from the producer, appends it to the file, and saves a **checkpoint** (the producer cursor and the file size) with the lease renewed. At the end it computes the SHA-256 of the file and marks the job `Completed`.
3. **Download.** `GET /chronoaudit/exports/{id}/download` streams the file with the header `X-Content-SHA256`. Each download writes an audit row (`ChronoAudit.ExportDownloaded`) *before* the first byte, so a download that cannot be recorded does not happen.

A job is visible to the person who requested it only; anyone else gets `404`.

### Several nodes, crashes, cancel

- Two workers may look at the same job at the same instant: the claim is guarded by the job's concurrency stamp, so exactly one wins and the other moves on. This was proven against real SQL Server and PostgreSQL with four workers racing six jobs.
- A worker that dies leaves a live lease nobody may take. When the lease has run out, the next worker continues **from the last checkpoint**: it cuts the file back to the checkpointed size and asks the producer for the next batch, so no batch is written twice. A job that fails with an exception is released and tried again; after 5 attempts it fails for good with `Export.Failed`.
- `POST /chronoaudit/exports/{id}/cancel` marks a `Pending` or `Running` job `Cancelled`; the worker notices at its next checkpoint, stops and deletes the half-written file.
- Files are kept for `RetentionDays` (default 7). Once an hour the worker deletes the files of older finished jobs. The job row stays; a later download says `Export.FileMissing`.

## 2. Formats

- **CSV**: UTF-8 with a BOM (so Excel reads it), RFC 4180 quoting, `\r\n` lines. A text cell that begins with `=`, `+`, `-`, `@`, a tab or a carriage return gets a leading apostrophe, so a spreadsheet cannot run it as a formula. Written straight to the file.
- **XLSX**: a real OpenXML workbook built by ChronoAudit's own streaming writer (no third-party package): numbers are numeric cells, text stays text, dates are dates, control characters are removed. The rows are first spooled to disk in batches and converted at the end, so a very large export does not sit in memory. One sheet holds at most 1,048,575 data rows; more fails the job with `Export.TooLarge`.

`MaxRows` (default 5,000,000) caps any export; an export over it fails with `Export.TooLarge` and its file is deleted.

## 3. The built-in producers

| Key | Parameters | Who may ask |
|---|---|---|
| `activity` | an `ActivitySearchQuery`: `from`, `to` (required, at most 366 days), the filters of the [activity search](http-endpoints.md) | All: every call; others: their own calls |
| `audit` | an `AuditSearchQuery`: `module`, `from`, `to` (at most 366 days), `actorUserId`, `resourceType`, `resourceId`, `action` | All only |
| `extract` | an `ExtractQuery`: `from`, `to`, `modules`, `tables`, `resourceType`, `actorUserId`, `includeTimeline` | All only |
| `analytics` | `{ "definition": <AnalyticsQuery> }` or `{ "savedReportId": "...", "from": "...", "to": "..." }` | whoever may run the subject; a saved report must be visible to the caller |
| `timeline` | `from`, `to` (required), `subjectType`, `subjectId`, `groupKey` | the events the caller may see |

An unknown producer is `400 Export.UnknownProducer`; a producer the caller may not use is `403 Export.KindForbidden`. Personal fields are masked in exports exactly as when read ([personal data](personal-data.md)).

```json
{ "producer": "activity", "format": "Csv",
  "parameters": { "from": "2026-10-01T00:00:00Z", "to": "2026-11-01T00:00:00Z", "statusClass": "5xx" } }
```

## 4. Your own producer

Implement `IExportProducer` and register it. A producer must page by a **unique, ordered key** (an id or a sequence), never by an offset or a timestamp alone, so rows with equal values keep a stable order across batches and a resumed job does not skip or repeat a row.

```csharp
using System.Text.Json;
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class MemberExportProducer(LibraryDbContext db) : IExportProducer
{
    public string Key => "library-members";

    public bool RequiresAllScope => true;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult(ChronoAuditResult.Success());

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>([new SheetColumn("Id"), new SheetColumn("Name", 30), new SheetColumn("Joined", 24)]);

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(
        JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        // The cursor is whatever you decide; here, the last id sent.
        var after = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);

        var page = await db.Set<Book>().AsNoTracking()
            .Where(b => b.Id > after)
            .OrderBy(b => b.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var rows = page
            .Select(b => (IReadOnlyList<SheetCell>)[SheetCell.Of(b.Id), SheetCell.Of(b.Title), SheetCell.Of(b.CreatedAtUtc)])
            .ToList();

        var done = page.Count < take;
        return new ExportBatchRows(rows, done ? null : page[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture), done);
    }
}

public static class ProducerRegistration
{
    public static void Register(ChronoAuditBuilder chrono) => chrono.AddExportProducer<MemberExportProducer>();
}
```

Rules:

- `Key` is 1 to 64 characters of `a-z`, `0-9` and `-`. Registering a key twice throws.
- `ValidateAsync` runs at request time, with the requester's scope: refuse here (bad parameters, a period that is too long, a scope that is not enough). `RequiresAllScope` is checked before it.
- `NextAsync` runs in the worker, possibly on another node and after a restart, with the scope **as it was at request time**. Apply that scope in your query. Return `Done = true` with the last batch; return a failed result for a predictable problem (it fails the job with that code); throw only for a fault.
- A producer is resolved from a fresh DI scope for each run of a job, so it can take a scoped `DbContext`.
- Return the same row for the same cursor: resume depends on it.

## 5. Options

| Option (`ChronoAudit:Exports:`) | Default | Meaning |
|---|---|---|
| `Directory` | `App_Data/chronoaudit-exports` | where the files are written; **must be shared storage when several nodes run workers or serve downloads** |
| `RetentionDays` | 7 | 1 to 365 |
| `LeaseSeconds` | 120 | 30 to 3600; a worker that stops renewing loses the job after this |
| `PollIntervalSeconds` | 5 | 1 to 300; the pause when the queue is empty |
| `MaxRows` | 5,000,000 | the most rows an export may hold |
| `MaxActivePerUser` | 3 | 1 to 50 |
| `BatchSize` | 1000 | 100 to 5000 |

The file name of a job is derived from its id; a name that is not valid is refused, so a download can never read outside `Directory`. Back up or ignore the directory as you see fit: the job row and its SHA-256 stay in the database.
