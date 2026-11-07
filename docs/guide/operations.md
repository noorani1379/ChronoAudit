# Operations

What runs where, what to watch, and what to do when something goes wrong.

## 1. Background workers and where to run them

Each worker is a hosted service you opt into on the builder. In a deployment with several instances of the application, not every worker should run on every instance.

| Registration | Does | Instances |
|---|---|---|
| `AddHashWorkers()` | adds pending audit, activity and intent rows to their HMAC chains | **one**. More than one is safe on SQL Server and PostgreSQL (they take a database lock and take turns) but wastes work; on an unknown provider the lock is not taken, so run exactly one |
| `AddAnchorWorker()` | appends the head of every chain to the anchor file, re-verifies the newest rows | **one** (the anchor file is local to the node) |
| `AddExportWorker()` | produces export files | as many as you need; they share jobs safely; the export directory must be **shared storage** if downloads can land on another node |
| `AddConsistencyWorker()` | runs your `IConsistencyCheck`s | **one** |

A common layout: web nodes call only `AddChronoAuditAspNetCore()`, `AddActivityLog()` and the feature registrations they serve, and one small worker service (same code, same database, same key) adds the four worker registrations. For a small application, register everything in the web host.

A web node that does not register the workers still writes audit rows, activity rows and intents; they stay *pending* until a worker elsewhere hashes them.

## 2. Database settings

- **SQL Server: turn on `READ_COMMITTED_SNAPSHOT`** for a database with heavy reads. Without it, a long analytics query or audit search takes shared locks that make the hash worker and your own writers wait.
  ```sql
  ALTER DATABASE [MyDatabase] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
  ```
- **PostgreSQL**: nothing special; the default isolation does not block readers.
- Create the guard triggers in your migration ([getting started](getting-started.md)). Give the application a database account without delete, alter or truncate rights ([security](security.md)).
- Plan retention yourself: ChronoAudit never deletes audit, activity or timeline rows. Partition or archive by date if a table grows past what you can query; keep the anchors and the key for as long as the rows matter.

## 3. Metrics

ChronoAudit publishes through `System.Diagnostics.Metrics` on the meter named `ChronoAudit` (add it to OpenTelemetry with `.AddMeter("ChronoAudit")`).

| Instrument | Type | Meaning | Alert when |
|---|---|---|---|
| `chronoaudit.audit.hash_backlog` (tag `module`) | observable gauge | audit rows waiting to be hashed | stays high for minutes: the hash worker is down, stuck or too slow |
| `chronoaudit.audit.hashed_rows` | histogram | rows hashed per round | drops to zero while the backlog grows |
| `chronoaudit.audit.hash_failures` | counter | hash rounds that failed | increasing |
| `chronoaudit.activity.retries` | counter | retried writes of the activity log | a sustained rate |
| `chronoaudit.activity.write_failures` | counter | activity rows that could not be stored (they went to the fallback log) | any increase |
| `chronoaudit.exports.completed`, `chronoaudit.exports.failed` | counters | finished and failed export jobs | `failed` increasing |
| `chronoaudit.exports.rows` | histogram | rows per completed export | for capacity planning |
| `chronoaudit.consistency.drifting_checks` | observable gauge | consistency checks that still drift after the grace wait | greater than zero |

The hash worker also logs a warning when a module has more than `ChronoAudit:Audit:BacklogWarnRows` (default 1000) unhashed rows.

## 4. Logs to alert on

Message templates (the property names are stable; match on the text before the first `{`):

**Critical** (treat as a security incident until proven otherwise):

- `Audit hash chain is broken: Module {Module}, first broken chain index ...`: a row was changed, deleted or inserted. Run `POST /chronoaudit/audit/verify` for details.
- `Audit anchors no longer match the database: ...`: newest rows were removed or changed.
- `Audit anchor file has {TamperedLines} edited, cut or inserted lines`: the anchor file itself was altered; compare with the off-server copy.
- `Audit chain of {Module} was hashed with another key ...` / `needs a key that is not configured ...  Hashing stopped.`: the HMAC key is not the one the chain was signed with. Put the old key in `ChronoAudit:PreviousHmacKeys` ([key rotation](hash-chain-and-anchors.md)). Hashing stays stopped until you do; rows keep accumulating as pending.

**Error** (operational problem):

- `Failed to hash audit rows for {Module}` and `Failed to hash the API activity rows`: the worker retries; look at the exception (a database outage, a lock timeout).
- `Audit anchor for {Module} ... could not be written` and `Audit anchoring failed`: the anchor directory is unreachable or full.
- `The API activity log row ... could not be written` and `The API activity fallback line ... could not be written`: calls are not being logged and the fallback also failed.
- `The mutation intent for {Method} {Path} could not be stored; the call is refused.`: the store is down; write calls get `503 ActivityLog.Unavailable`.
- `Export job {JobId} failed on attempt {Attempt}` and `The export worker iteration failed`.
- `A consistency check failed`.

**Warning**: `Audit hash backlog`, `Consistency check ... drift` (with both counts), `ChronoAudit does not know the database provider ...`.

At start-up the host logs `ChronoAudit chain key fingerprint {Fingerprint}`: compare it across nodes; all nodes must show the same value.

## 5. Backups and disaster recovery

Back up, separately and regularly:

1. **The database** (it holds the audit rows and the store).
2. **The HMAC keys**: the current one and every key in `PreviousHmacKeys`, in a secret manager, not next to the database backup. Without them a restored chain cannot be verified.
3. **The anchor directory** (see below), off the server.
4. **The export directory** only if you need old files; jobs and their SHA-256 are in the database and files are deleted after `RetentionDays` anyway.

After restoring a database backup, run `verify` for every chain and compare with the anchors: if the restore is older than the newest anchor, `anchorMismatches` lists the chain indexes that are missing, which is exactly how you learn how far back the restore went.

**Anchor storage**: map `ChronoAudit:Anchors:Directory` to storage the application can append to but not silently rewrite (a write-once bucket, a network share with versioning), or copy the file off the server on a schedule.

## 6. Runbook

| Symptom | Likely cause | Action |
|---|---|---|
| Backlog gauge rises, nothing hashed | no hash worker running; key mismatch (Critical log); database lock held | check that one node runs `AddHashWorkers()`; read the logs; fix the key |
| `503 ActivityLog.Unavailable` on writes | the store tables or database are unavailable | fix the database; the calls were refused on purpose, nothing was changed |
| `Filter.NotAllowed` for a valid-looking call | a misspelled or unsupported query key | the message names the key |
| Export stays `Pending` | no export worker, or `MaxActivePerUser` reached | add `AddExportWorker()` on a node; wait for or cancel the other jobs |
| Export `Failed` with `Export.FileCorrupt` | the half-written file was lost (different disk or node) | use shared storage for `Exports:Directory`; ask for a new export |
| Download says `Export.FileMissing` | older than `RetentionDays` | request a new export |
| `Chain.UnknownKey` when rotating | the old key is not listed | add it to `PreviousHmacKeys` |
| Verification broken at one index | tampering, or a restore from an older backup | compare with the anchors and the backup; treat as an incident until explained |
| `anchorMismatches` right after you re-created a database | the anchor directory still holds the heads of the old database | in a throw-away environment delete the anchor directory when you re-create the database; in production treat a mismatch as real |
