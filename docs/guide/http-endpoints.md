# HTTP endpoints

`app.MapChronoAudit("<policy>")` maps eight route groups under `/chronoaudit`. You can map the groups one by one with their own policy and base path instead (`MapChronoAuditHistory`, `MapChronoAuditAudit`, `MapChronoAuditActivity`, `MapChronoAuditAnalytics`, `MapChronoAuditSavedReports`, `MapChronoAuditExports`, `MapChronoAuditExtract`, `MapChronoAuditTimeline`). A group whose feature was not registered (`AddExports()` missing, for example) answers 500, so map only what you added.

The JSON in this page is real output of the library's own test host, shortened where marked.

## Conventions

**Authorization.** Every route is behind the authorization policy you pass; a blank policy name throws at startup. An anonymous call gets 401, a policy that refuses gets 403.

**Access scope.** Besides the policy, each call has a data scope, decided by `IChronoAuditAccessScope`. The default reads the claims of the user: `chronoaudit_scope=all` gives **All**, a `group` claim gives **Group**, anything else is **Own**. Replace the service to decide differently. Claim names are options (`ChronoAudit:AspNetCore:ScopeClaimType`, `GroupIdClaimType`, ... read by `AddChronoAuditAspNetCore(configuration)`; see the [configuration reference](configuration.md)). What each route does per scope is in the tables below; a route that needs All answers `403 ChronoAudit.ScopeDenied` (or `404` when the existence of the thing must not be revealed).

**JSON.** `camelCase` names, enums as strings (`"Csv"`, `"Completed"`), times as UTC ISO 8601.

**Errors.** `application/problem+json`: `title` is the stable code, `detail` the message, `status` the HTTP status. See [error codes](error-codes.md). Kind to status: validation 400, unauthorized 401, forbidden 403, not found 404, conflict 409, unprocessable 422, unavailable 503. No stack trace or SQL ever appears.

```json
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1", "title": "Filter.NotAllowed", "status": 400,
  "detail": "The filter or sort 'typo' is not allowed for this report." }
```

**Unknown query keys are refused.** Every GET route lists the query keys it understands; any other key gets `400 Filter.NotAllowed` before anything is read, so a misspelled filter never silently returns everything.

**Paging.** Lists return `{ "items": [...], "nextCursor": "..." }`. Pass `nextCursor` back as `cursor` (activity, timeline: `before`) for the next page; it is `null` on the last. Cursors are opaque and tamper-checked: a changed one gives `400 ChronoAudit.InvalidCursor`. `limit` is 1 to 200 (default 50; 20 for the list of exports, 100 for incomplete intents).

**Response headers.**

| Header | When |
|---|---|
| `X-Correlation-Id` | every call; send the same header on the request to choose the id (it must be a GUID, else a new one is made) |
| `X-Data-Lag-Seconds` | read calls (GET) that succeeded, when you registered an `IChronoAuditDataLagSource` and it knows the lag: how many seconds the oldest unprocessed event waits (see [timeline](timeline.md)) |
| `X-Content-SHA256` | export download: the SHA-256 (hex) of the file |
| `Content-Disposition` | file downloads |

A browser can read the last three only if your CORS policy exposes them: `policy.WithExposedHeaders(ChronoAuditHttpHeaders.ExposedToBrowsers.ToArray())`.

**Every call is recorded** in the [activity log](activity-log.md) when the middleware is in the pipeline.

---

## History: `/chronoaudit/history`

Field-level change history. Needs the **All** scope (others: `404 Table.NotFound`). The catalog is open to any authorized caller.

| Route | Query | Result |
|---|---|---|
| `GET tables` | none | the modules, tables, keys and columns that can be read |
| `GET {module}/tables/{table}/changes` | `from`, `to`, `cursor`, `limit`, `reveal` | a page of changes of a table, oldest first |
| `GET {module}/tables/{table}/records/{key}/changes` | `cursor`, `limit`, `reveal` | a page of changes of one record; a composite key is joined with `\|` |

`reveal=true` asks for personal fields unmasked; see [personal data](personal-data.md). Errors: `Table.NotFound`, `Record.NotFound`, `ChronoAudit.InvalidCursor`, `ChronoAudit.Validation`.

```json
{
  "items": [
    {
      "sequence": 1,
      "occurredAtUtc": "2026-11-01T08:00:00+00:00",
      "action": "Auto_Added",
      "actorUserId": "System",
      "actorDisplayName": null,
      "onBehalfOfUserId": null,
      "resourceType": "Orders",
      "recordKey": "d9118a64-735a-4625-9f51-3f96fc7ec27a",
      "fields": [
        { "field": "Id", "from": null, "to": "d9118a64-735a-4625-9f51-3f96fc7ec27a" },
        { "field": "Secret", "from": null, "to": "***MASKED***" }
      ]
    }
  ],
  "nextCursor": null,
  "personalDataRevealed": false,
  "asOfUtc": "2026-11-01T08:00:02+00:00"
}
```

## Audit: `/chronoaudit/audit`

Needs the **All** scope.

| Route | Input | Result |
|---|---|---|
| `GET {module}/search` | `from`, `to` (required, `Analytics.RangeRequired` without them), `actor`, `resourceType`, `resourceId`, `action`, `cursor`, `limit` | raw audit rows, newest first; at most 31 days, or 366 with `actor` or `resourceId` |
| `POST verify` | body `{ "module": "shop", "afterChainIndex": 0, "maxRows": 10000 }` (only `module` is required) | the result of re-walking the HMAC chain |

`verify` records its own run (`ChronoAudit.ChainVerified`) before it starts and does not run when that cannot be recorded. An unknown module is `404 Chain.UnknownModule`. Call it again with `nextAfterChainIndex` to continue a long chain in steps.

```json
{ "valid": true, "verifiedCount": 2, "pendingCount": 1, "failedAtChainIndex": null, "failedAtSequence": null,
  "failure": null, "nextAfterChainIndex": null, "anchorsChecked": 0, "anchorMismatches": [] }
```

A row of `search` (`items`, `nextCursor`):

```json
{ "sequence": 2, "occurredAtUtc": "2026-11-01T08:00:01+00:00", "actorUserId": "System", "action": "Auto_Added",
  "resourceType": "Orders", "resourceId": "{\"Id\":\"7a04f0ff-...\"}", "oldValues": null, "newValues": "{...}",
  "metadata": null, "previousHash": "2A1D...", "payloadHash": "3A45..." }
```

## Activity log: `/chronoaudit/activity`

One row per API call (see [activity log](activity-log.md)). A caller without the All scope sees only their own calls; asking for another `userId` is `403 ChronoAudit.ScopeDenied`.

| Route | Query | Result |
|---|---|---|
| `GET search` | `from`, `to` (required; at most 31 days, or 366 when `userId`, `sessionId` or `correlationId` is given), `userId`, `sessionId`, `correlationId`, `endpointKey`, `method`, `statusCode`, `statusClass` (`2xx`..`5xx`), `ip`, `incompleteOnly`, `before`, `limit` | a page of calls, newest first |
| `GET export.csv` | the same filters without paging; `from` and `to` required, same range rule as the search | a CSV file with a BOM, at most 100 000 rows (`Export.TooLarge` above that); the export is recorded before the first byte |
| `GET statistics` | `from`, `to` (required, at most 366 days), `by` = `day` (default), `month`, `endpoint`, `statusClass` or `user` (All scope only) | counts per bucket |
| `GET requests/{logId}/body` | none | the stored, masked body of one call; **All** scope; viewing it is audited first |
| `GET intents/incomplete` | `limit` | mutation intents that never got a result (a crash between intent and answer); **All** scope |

```json
{ "items": [ { "id": 7, "requestId": "4a37decf-...", "occurredAtUtc": "2026-11-01T08:00:02+00:00", "durationMs": 60,
  "userId": "admin", "actAsUserId": null, "sessionId": "s-admin", "correlationId": "60b6814a-...", "httpMethod": "POST",
  "path": "/chronoaudit/audit/verify", "endpointKey": "chronoaudit/audit/verify", "statusCode": 200, "errorCode": null,
  "ipAddress": null, "hasBody": true, "previousHash": null, "payloadHash": null } ],
  "nextCursor": "aTE6Nw", "asOfUtc": "2026-11-01T08:00:02+00:00" }
```

Statistics: `{ "buckets": [ { "key": "2026/11/01", "count": 8 } ], "total": 8, "isCapped": false }`. Body: `{ "logId": 2, "requestId": "...", "contentType": "application/json; charset=utf-8", "body": "{\"title\":\"Dune\"}", "isTruncated": false, "fileMetadata": null }`.

## Analytics: `/chronoaudit/analytics`

| Route | Body | Result |
|---|---|---|
| `GET catalog` | none | the subjects the caller may use, with their dimensions, measures and filter fields |
| `POST run` | an `AnalyticsQuery` | the grouped result |

The query, the subjects and the limits are explained in the [analytics guide](analytics.md). Example body and result:

```json
{ "subject": "activity", "dimensions": [ "method" ], "measures": [ "count" ],
  "from": "2026-10-31T08:00:00+00:00", "to": "2026-11-02T08:00:00+00:00" }
```

```json
{ "dimensions": [ { "key": "method", "title": "Method" } ], "measures": [ { "key": "count", "title": "Calls" } ],
  "rows": [ { "dimensions": [ "GET" ], "measures": [ 11 ], "previous": null }, { "dimensions": [ "POST" ], "measures": [ 2 ], "previous": null } ],
  "isTruncated": false, "from": "2026-10-31T08:00:00+00:00", "to": "2026-11-02T08:00:00+00:00", "previousFrom": null, "previousTo": null }
```

Errors: `Analytics.SubjectNotFound` (404), `Analytics.SubjectForbidden` (403), `Analytics.RangeRequired`, `Analytics.RangeTooWide`, `Filter.NotAllowed` (400).

## Saved reports: `/chronoaudit/saved-reports`

A saved report is a stored analytics query. It belongs to its creator and can be shared with a group. See the [saved reports guide](saved-reports.md).

| Route | Input | Result |
|---|---|---|
| `GET` | `limit` | the caller's reports and the ones shared with them (without the definition) |
| `POST` | `{ "title": "...", "definition": <AnalyticsQuery>, "sharedWithGroupId": null }` | `201` with the report |
| `GET {id}` | none | the report with its definition; another user's private report is `404 SavedReport.NotFound` |
| `PUT {id}` | `{ "title", "definition", "sharedWithGroupId", "expectedStamp" }` | the changed report; a stale `expectedStamp` is `409 SavedReport.Conflict` |
| `DELETE {id}?expectedStamp=` | the stamp in the query | `204` |
| `POST {id}/run` | optional `{ "from": ..., "to": ... }` | the analytics result for the stored query, optionally over another period |

`expectedStamp` is the `concurrencyStamp` you last read: two people editing the same report cannot overwrite each other.

## Exports: `/chronoaudit/exports`

Large files are produced by a background worker. See the [exports guide](exports.md).

| Route | Input | Result |
|---|---|---|
| `POST` | `{ "producer": "activity", "format": "Csv", "parameters": { ... } }` | `201` with the job (state `Pending`) |
| `GET` | `limit` | the caller's jobs |
| `GET {id}` | none | the job; another user's job is `404` |
| `POST {id}/cancel` | none | the job, now `Cancelled`, while it is `Pending` or `Running`; a finished job is `Export.AlreadyFinished` |
| `GET {id}/download` | none | the file (`text/csv; charset=utf-8` or the XLSX type) with `X-Content-SHA256`; each download is audited; `Export.NotReady`, `Export.FileMissing` otherwise |

```json
{ "id": "262c83be-...", "producer": "activity", "format": "Csv", "state": "Completed", "rowCount": 6, "fileSizeBytes": 965,
  "sha256": "7B08BFC2...", "errorCode": null, "errorMessage": null,
  "createdAtUtc": "2026-11-01T08:00:02+00:00", "startedAtUtc": "2026-11-01T08:00:02+00:00", "finishedAtUtc": "2026-11-01T08:00:02+00:00" }
```

States: `Pending`, `Running`, `Completed`, `Failed`, `Cancelled`.

## Extract: `/chronoaudit/extract`

`GET csv` streams the audit rows of the modules in one pass (for a data warehouse or an auditor). **All** scope. Query: `from`, `to` (required), `modules` (comma separated), `resourceType`, `actor`, `includeTimeline`, `cursor`. The run is recorded before the first byte (`503 ChronoAudit.AuditUnavailable` and nothing written when it cannot be), and a second record with the row count and the SHA-256 of what was sent is written at the end, also when the connection was cut.

## Timeline: `/chronoaudit/timeline`

Business events recorded by your code, see the [timeline guide](timeline.md).

| Route | Query | Result |
|---|---|---|
| `GET subjects/{subjectType}/{subjectId}` | `eventTypes` (comma separated, at most 20), `from`, `to`, `before`, `limit` | a page of events of one subject, newest first |
| `GET groups/{groupKey}` | the same | the events of a group (a case, a dossier) |
| `GET actors/{actorUserId}` | the same | the events a user took part in |
| `GET subjects/{subjectType}/{subjectId}/evidence` | none | the evidence bundle (JSON): events, audit rows, chain checks and a SHA-256 of the data; **All** scope; issuing it is audited first |

What the caller sees depends on the scope: **All** everything, **Group** the events of their access group, **Own** the events they acted in or took part in.

```json
{ "items": [ { "id": 1, "sourceEventId": "649f6580-...", "eventType": "order.placed", "title": "order.placed",
  "subjectType": "Order", "subjectId": "o-1", "groupKey": "case-7", "occurredAtUtc": "2026-11-01T08:00:00+00:00",
  "actorDisplayName": "Alice", "actorRole": null, "onBehalfOfUserId": null, "payload": "{\"total\":120}" } ],
  "nextCursor": null }
```
