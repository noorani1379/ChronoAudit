# Analytics

The analytics engine groups and counts rows (audit rows, API calls, timeline events, or your own tables) by up to three dimensions and four measures, in **one SQL statement**. A request names things from a closed catalog; nothing in a request is ever turned into a column name or an expression, so a caller cannot reach a column you did not publish.

`AddBuiltInAnalytics()` registers the built-in subjects. `AddAnalyticsSubject` adds your own.

## 1. The built-in subjects

| Subject | Rows | Who may use it |
|---|---|---|
| `audit:{module}` (one per `AddModule`) | the audit rows of that module | All scope only |
| `activity` (needs `AddActivityLog`) | the API calls | All sees every call; anyone else (Group is treated as Own) sees only their own calls |
| `timeline` (needs `AddTimeline`) | the business events | the events the caller may see (All, Group, Own rules of the [timeline](timeline.md)) |

| Subject | Dimensions | Measures | Filter fields (operators) |
|---|---|---|---|
| `audit:{module}` | `action`, `resourceType`, `actor`, `day`, `month`, `quarter`, `year` | `count`, `distinctActors`, `distinctResources` | `action`, `resourceType`, `actor` (Eq, In); `resourceId` (Eq) |
| `activity` | `day`, `month`, `quarter`, `year`, `endpoint`, `method`, `statusClass`, `user`, `errorCode` | `count`, `distinctUsers`, `avgDurationMs`, `maxDurationMs`, `errors` (server errors, 5xx) | `endpoint` (Eq, In, Prefix); `status` (Range); `user`, `method` (Eq, In); `errorCode` (Eq, IsNull) |
| `timeline` | `eventType`, `subjectType`, `actorRole`, `day`, `month`, `quarter`, `year` | `count`, `distinctSubjects`, `distinctActors` | `eventType` (Eq, In, Prefix); `subjectType` (Eq, In); `actor`, `groupKey` (Eq) |

`GET /chronoaudit/analytics/catalog` returns the same list for the caller, so a front end can build its pickers from it.

**Time grains.** `day`, `month`, `quarter` and `year` use the calendar columns stored on the rows. Set the calendar once, before the first row exists (`ChronoAudit:Calendar:Kind` = `Gregorian` or `Persian`, and `TimeZoneId`, default `UTC`); a "day" is the day in that time zone. Changing it later regroups new rows only.

## 2. Running a query

A query is a subject, 1 to 3 dimensions, 1 to 4 measures, an optional filter, and a time range (required, at most 36 months).

```json
{
  "subject": "activity",
  "dimensions": [ "endpoint", "statusClass" ],
  "measures": [ "count", "avgDurationMs" ],
  "filters": [
    { "field": "method", "op": "In", "values": [ "POST", "PUT" ] },
    { "or": [
        { "field": "endpoint", "op": "Prefix", "values": [ "books" ] },
        { "and": [ { "field": "status", "op": "Range", "values": [ "500", "" ] } ] }
    ] }
  ],
  "from": "2026-10-01T00:00:00Z",
  "to": "2026-11-01T00:00:00Z",
  "topN": 100,
  "sortBy": "count",
  "sortDescending": true,
  "comparePrevious": true
}
```

| Field | Meaning |
|---|---|
| `topN` | rows returned, 1 to 500 (default 100); `isTruncated` says there were more groups |
| `sortBy` | a measure key or a dimension key; by default the first measure, descending |
| `comparePrevious` | also computes the same measures for the period of equal length right before `from`; each row then has `previous` |

**Filters.** The root is an implicit AND. A node is one condition (`field`, `op`, `values`), an `or` group or an `and` group, nested at most three levels deep: root, then condition or `or` group; an `or` member is a condition or an `and` group; an `and` member is a condition. Limits: 20 conditions, 10 members per `or`, 50 values in an `In`, 64 characters in a `Prefix`. Operators: `Eq` (one value), `In` (1 to 50), `Range` (two bounds, inclusive; an empty bound is open), `Prefix` (text starts with; never "contains"), `IsNull`. A field or operator outside the catalog is `400 Filter.NotAllowed` naming it. Text fields compare after normalization (Persian and Arabic `ی`/`ي`, `ک`/`ك` and digits are the same).

**Result.**

```json
{
  "dimensions": [ { "key": "method", "title": "Method" } ],
  "measures": [ { "key": "count", "title": "Calls" } ],
  "rows": [ { "dimensions": [ "GET" ], "measures": [ 11 ], "previous": null }, { "dimensions": [ "POST" ], "measures": [ 2 ], "previous": null } ],
  "isTruncated": false,
  "from": "2026-10-31T08:00:00+00:00", "to": "2026-11-02T08:00:00+00:00", "previousFrom": null, "previousTo": null
}
```

A row's `dimensions` and `measures` follow the order of the request. A group whose dimension value is unknown is one group with a `null` label. A time grain's label is formatted by the grain (for example `2026/11/01` for a day).

From code, inject `IAnalyticsService`:

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;

public sealed class CallsReport(IAnalyticsService analytics)
{
    public async Task<AnalyticsResult> CallsByMethodAsync(string adminUserId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var query = new AnalyticsQuery(
            Subject: "activity",
            Dimensions: ["method"],
            Measures: ["count"],
            Filters: [FilterNode.Condition("status", FilterOp.Range, "400", "499")],
            From: from,
            To: to);

        var result = await analytics.RunAsync(query, ChronoAuditScope.Everything(adminUserId), ct);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Code + ": " + result.Error.Message);
    }
}
```

Errors are values: `Analytics.SubjectNotFound`, `Analytics.SubjectForbidden`, `Analytics.RangeRequired`, `Analytics.RangeTooWide`, `Filter.NotAllowed`, `ChronoAudit.Validation` (dimension, measure or `topN` counts).

## 3. Your own subject

A subject has a key, a range column (the time column the required range limits), dimensions, measures and filter fields. You also say where its rows come from; that source **must apply the access scope**, because it is the only thing standing between a Group or Own caller and everyone's rows.

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class Loan : AuditableEntityBase
{
    public int BookId { get; set; }

    public int MemberId { get; set; }

    public DateTimeOffset LoanedAtUtc { get; set; }

    public string Branch { get; set; } = "main";

    public decimal LateFee { get; set; }
}

public static class LoanAnalytics
{
    public static AnalyticsSubject<Loan> Subject() => new(
        key: "library:loans",
        title: "Loans",
        range: l => l.LoanedAtUtc,
        dimensions:
        [
            new AnalyticsDimension<Loan>("branch", "Branch", l => l.Branch),
            AnalyticsDimension.Numeric<Loan>("month", "Month", l => (long?)((l.LoanedAtUtc.Year * 100L) + l.LoanedAtUtc.Month), TimeGrain.Month),
        ],
        measures:
        [
            AnalyticsMeasure<Loan>.Count("count", "Loans"),
            AnalyticsMeasure<Loan>.CountDistinct("members", "Distinct members", l => l.MemberId.ToString()),
            AnalyticsMeasure<Loan>.Of("fees", "Late fees", Aggregation.Sum, l => (decimal?)l.LateFee),
        ],
        filters:
        [
            FilterField.Of<Loan, string>("branch", "Branch", l => l.Branch, FilterOp.Eq, FilterOp.In),
            FilterField.Of<Loan, decimal>("lateFee", "Late fee", l => l.LateFee, FilterOp.Range),
        ])
    {
        RequiresAllScope = false,
    };

    // The scope is applied here: All sees all loans; anyone else only the loans they created.
    public static IQueryable<Loan> Source(IServiceProvider services, ChronoAuditScope scope)
    {
        var loans = services.GetRequiredService<LibraryDbContext>().Set<Loan>().AsNoTracking();
        return scope.Kind == ScopeKind.All ? loans : loans.Where(l => l.CreatedBy == scope.UserId);
    }

    public static void Register(ChronoAuditBuilder chrono)
        => chrono.AddAnalyticsSubject(Subject(), Source);
}
```

Rules for a subject:

- The range column and every dimension, measure and filter are *expressions you write*; they must translate to SQL (no method calls EF cannot translate). Use a normalized column for text filters (`FilterField.NormalizedText`).
- A time grain needs a numeric key and a label format: `AnalyticsDimension.Numeric` plus `TimeGrain.Day`, `Month`, `Quarter` or `Year` (the number is `yyyymmdd`, `yyyymm`, `yyyyq` or `yyyy`).
- An enum column becomes a text dimension with `AnalyticsDimension.EnumName`.
- Mark `RequiresAllScope = true` for a subject only administrators may use.
- A subject key must be unique; registering the same key twice throws.
- Group and measure counts are bounded (3 dimensions, 4 measures, 500 rows), so a request cannot ask for an unbounded result.
