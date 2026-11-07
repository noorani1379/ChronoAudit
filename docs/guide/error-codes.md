# ChronoAudit Error Codes Reference

All predictable errors in ChronoAudit return a `ChronoAuditResult` / `ChronoAuditResult<T>` containing a typed `ChronoAuditError`. In ASP.NET Core endpoints, these map directly to RFC 7807 `ProblemDetails` with the error code as the `Title` and the appropriate HTTP status code.

---

## Error Classification & HTTP Mapping

| Kind (`ChronoAuditErrorKind`) | HTTP Status | Description |
|---|---|---|
| `Validation` | `400 Bad Request` | Client passed invalid arguments, unknown parameters, or exceeded limits |
| `Unauthorized` | `401 Unauthorized` | No signed-in user (the policy of your host normally answers this before ChronoAudit does) |
| `Forbidden` | `403 Forbidden` | The caller's `ChronoAuditScope` lacks required permissions |
| `NotFound` | `404 Not Found` | The requested entity, report, job, or resource does not exist or is invisible |
| `Conflict` | `409 Conflict` | Concurrency collision, duplicate state, or conflicting workflow transition |
| `Unprocessable` | `422 Unprocessable Entity` | Valid syntax, but semantically incompatible or outdated definition |
| `Unavailable` | `503 Service Unavailable` | Storage or audit mechanism unavailable; fails closed |

---

## Error Codes Catalog

| Error Code | Kind | HTTP Status | Situation / Meaning |
|---|---|---|---|
| `ChronoAudit.Validation` | Validation | 400 | General validation failure on input parameters (field name and rule provided in message). |
| `Analytics.RangeRequired` | Validation | 400 | The range (`from` and `to`) is required. |
| `Analytics.RangeTooWide` | Validation | 400 | Analytics date range exceeds the configured maximum window. |
| `Filter.NotAllowed` | Validation | 400 | Unknown dimension, invalid operator, excessive filter nesting (> 3 levels), or too many values. |
| `Analytics.SubjectNotFound` | NotFound | 404 | The requested analytics subject does not exist in the catalog. |
| `Analytics.SubjectForbidden` | Forbidden | 403 | The requested analytics subject requires elevated permissions (`ScopeKind.All`). |
| `SavedReport.TitleInvalid` | Validation | 400 | Saved report title is empty, whitespace, or exceeds maximum length. |
| `SavedReport.DefinitionInvalid` | Validation | 400 | Saved report query definition is invalid or cannot be serialized. |
| `SavedReport.NotFound` | NotFound | 404 | Saved report does not exist or is outside the caller's access scope. |
| `SavedReport.NotOwner` | Forbidden | 403 | User attempted to modify or delete a saved report owned by another user. |
| `SavedReport.ShareNotAllowed` | Forbidden | 403 | User attempted to share a report into a group they do not belong to. |
| `SavedReport.Conflict` | Conflict | 409 | Concurrent modification detected on saved report. |
| `SavedReport.Outdated` | Unprocessable | 422 | Stored report definition references dimensions or filters that are no longer valid. |
| `Export.NotFound` | NotFound | 404 | Export job does not exist or is outside caller's access scope. |
| `Export.FileMissing` | NotFound | 404 | Export artifact has been pruned by retention cleanup or is missing from disk. |
| `Export.KindForbidden` | Forbidden | 403 | The export producer requires elevated permissions (`ScopeKind.All`). |
| `Export.TooManyActive` | Conflict | 409 | User has exceeded the concurrent active export jobs quota. |
| `Export.NotReady` | Conflict | 409 | The job is still `Pending` or `Running`, or did not produce a file. |
| `Export.AlreadyFinished` | Conflict | 409 | Attempted to cancel an export job that has already completed or failed. |
| `Export.Conflict` | Conflict | 409 | Concurrent state modification on export job. |
| `Export.TooLarge` | Validation | 400 | The export is over `MaxRows` (or the 1,048,575 rows an XLSX sheet holds). Stored as the `errorCode` of the failed job; an activity CSV export over 100 000 rows is refused with this code at request time. |
| `Export.Failed` | - | - | Stored as the `errorCode` of a job that failed for good (after 5 attempts, or its producer is gone). Not an HTTP response. |
| `Export.UnknownProducer` | Validation | 400 | The specified export producer is not registered in the system. |
| `Export.FileCorrupt` | - | - | Stored as the `errorCode` of a job whose half-written file is missing, shorter than its checkpoint. Not an HTTP response. |
| `Table.NotFound` | NotFound | 404 | The requested module or entity table was not found or is out of scope. |
| `Record.NotFound` | NotFound | 404 | No change history exists for the requested record key. |
| `Table.RecordLookupNotSupported` | Unprocessable | 422 | Entity table does not support single-key record history lookups (e.g., complex composite key). |
| `ChronoAudit.InvalidCursor` | Validation | 400 | Provided keyset pagination cursor is malformed or invalid. |
| `Extract.InvalidTable` | Validation | 400 | Malformed table name or format passed to extract endpoint. |
| `ActivityLog.NotFound` | NotFound | 404 | The requested HTTP activity log entry or request body was not found. |
| `ActivityLog.Unavailable` | Unavailable | 503 | Could not persist mutation intent; request execution refused to prevent untracked side effects. |
| `ChronoAudit.AuditUnavailable` | Unavailable | 503 | Fail-closed: audit record for a sensitive view or export could not be written, so data was not returned. |
| `Evidence.TooLarge` | Validation | 400 | Requested evidence bundle exceeds the maximum allowed event count or file size. |
| `Evidence.NotFound` | NotFound | 404 | The requested subject for evidence bundle generation was not found or is out of scope. |
| `Timeline.InvalidEvent` | Validation | 400 | Timeline event input is missing required fields (SourceEventId, EventType, SubjectType, or SubjectId). |
| `Chain.UnknownModule` | NotFound | 404 | The specified audit module does not exist. |
| `ChronoAudit.ScopeDenied` | Forbidden | 403 | Access denied: caller lacks the required `ChronoAuditScope`. |
| `Chain.UnknownKey` | Conflict | 409 | No configured key verifies the newest hashed row; add the old key to `ChronoAudit:PreviousHmacKeys`. |
| `Chain.NothingToRotate` | Conflict | 409 | The chain has no hashed row yet, or already uses the current key. |
| `Chain.RotationPending` | Conflict | 409 | A key rotation marker is still waiting to be hashed. |
