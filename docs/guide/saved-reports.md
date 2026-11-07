# Saved reports

A saved report is an [analytics query](analytics.md) with a title, stored in the database so a person can run it again with one click, with a new period if they like. Reports belong to their creator and can be shared with the creator's group.

Needs `UseStore<T>()` and `AddSavedReports()` (and `AddBuiltInAnalytics()` or your own subjects, since a report runs a subject).

## 1. Who sees what

| Caller scope | Sees |
|---|---|
| Own | their own reports |
| Group | their own reports and the reports shared with their group |
| All | their own reports and every shared report |

A report that is outside what the caller may see is `404 SavedReport.NotFound`, the same answer as for a report that does not exist, so ids cannot be probed. Only the **owner** can change or delete a report (`403 SavedReport.NotOwner` for anyone else who can see it). A report can be shared only with the caller's own group (`403 SavedReport.ShareNotAllowed`). A report needs a signed-in owner.

Running a saved report runs its query **with the caller's scope**, not the owner's: a Group caller running a shared report sees their group's numbers. If the caller may not use the report's subject, the run fails with `Analytics.SubjectForbidden`.

## 2. Over HTTP

| Route | Notes |
|---|---|
| `POST /chronoaudit/saved-reports` | body `{ "title", "definition", "sharedWithGroupId" }`; `201` with the report; title 1 to 200 characters (`SavedReport.TitleInvalid`); the definition must be a valid query (`SavedReport.DefinitionInvalid`) |
| `GET /chronoaudit/saved-reports?limit=` | up to 100 reports, without the definition (to keep the list small) |
| `GET /chronoaudit/saved-reports/{id}` | the report with its `definition` |
| `PUT /chronoaudit/saved-reports/{id}` | body `{ "title", "definition", "sharedWithGroupId", "expectedStamp" }` |
| `DELETE /chronoaudit/saved-reports/{id}?expectedStamp=` | `204` |
| `POST /chronoaudit/saved-reports/{id}/run` | optional body `{ "from": "...", "to": "..." }` to run over another period |

Every report carries a `concurrencyStamp`. Send the one you read as `expectedStamp` when you update or delete: if someone changed the report meanwhile you get `409 SavedReport.Conflict` and can reload, instead of silently overwriting their change. A definition saved against an older catalog that no longer fits (a removed dimension) runs as `422 SavedReport.Outdated` and must be saved again.

```json
{
  "id": "5fe6038e-dbff-4ca1-8409-4d10e2cc5617",
  "title": "Calls by method",
  "subject": "activity",
  "definition": { "subject": "activity", "dimensions": [ "method" ], "measures": [ "count" ], "filters": null,
    "from": "2026-10-31T08:00:00+00:00", "to": "2026-11-02T08:00:00+00:00", "topN": 100, "sortBy": null, "sortDescending": true, "comparePrevious": false },
  "isShared": false,
  "isMine": true,
  "createdAtUtc": "2026-11-01T08:00:02+00:00",
  "updatedAtUtc": "2026-11-01T08:00:02+00:00",
  "concurrencyStamp": "e44f5229-2ed9-4cea-bbfa-c627ec489f4e"
}
```

A saved report has a fixed `from` and `to` in its definition. To make a "this month" report, keep the saved range as an example and pass the new `from` and `to` on every run.

## 3. From code

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;

public sealed class WeeklyDigest(ISavedReportService reports)
{
    public async Task<AnalyticsResult> RunLastWeekAsync(Guid reportId, string userId, DateTimeOffset now, CancellationToken ct)
    {
        var scope = ChronoAuditScope.OwnOf(userId);

        var result = await reports.RunAsync(reportId, now.AddDays(-7), now, scope, ct);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Code);
    }

    public async Task<SavedReportDto> SaveAsync(string userId, AnalyticsQuery definition, CancellationToken ct)
    {
        var created = await reports.CreateAsync(new SavedReportInput("Weekly digest", definition), ChronoAuditScope.OwnOf(userId), ct);
        return created.IsSuccess ? created.Value : throw new InvalidOperationException(created.Error.Code);
    }
}
```

`UpdateAsync(id, input, expectedStamp, scope, ct)` and `DeleteAsync(id, expectedStamp, scope, ct)` take the stamp as a `Guid?`; passing `null` skips the concurrency check, which you should do only for a single-writer batch job.

`SavedReports` is an ordinary table of the store context. When that context is also an audited module (as in the sample), creating, changing and deleting a report appear in the audit trail like any other change; exporting a report is audited as an export ([exports](exports.md)).
