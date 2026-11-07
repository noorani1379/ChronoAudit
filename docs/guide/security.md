# Security model

What ChronoAudit protects, how, and what it deliberately does not.

## 1. Threats and defenses

| Threat | Defense |
|---|---|
| Application code (or a bug) edits or deletes audit rows | **Guard triggers** (SQL Server, PostgreSQL, SQLite) refuse the change, see section 2. |
| Someone with database rights disables a trigger and edits history | **HMAC chain.** Every row is chained with a secret key; an edited row or a broken link fails verification, and without the key a valid replacement cannot be forged ([hash chain](hash-chain-and-anchors.md)). |
| The **newest** rows are deleted (the rest still verifies) or the table is truncated | **Anchors**: the head of each chain is copied, with a chained MAC, to files outside the database. A mismatch is a Critical log. |
| Secrets in API traffic end up in the log | **Name-based masking** of bodies, query strings and form fields before anything is stored; binary bodies and file contents are never stored; request headers are not stored at all (only the user agent) ([activity log](activity-log.md)). |
| Secrets in your tables end up in audit rows | `[SensitiveData]` stores `***MASKED***`; the value never reaches the audit table. |
| Staff read customers' personal data through the history | **Fail-closed masking** of the personal fields you list; a reveal needs your `IPersonalDataRevealPolicy`, and the audit row of the reveal is written *before* the data is returned ([personal data](personal-data.md)). |
| A user reads or infers another user's data (BOLA / IDOR) | The **access scope** (All, Group, Own) is applied inside the SQL `WHERE` of every query. A report, export or table the caller may not see answers `404`, the same as one that does not exist. |
| Forged query parameters | Every GET route accepts only the keys it lists; others are `400 Filter.NotAllowed`. Analytics, sorts and filters name things from a closed catalog; nothing in a request ever becomes a column name or an expression. |
| Resource exhaustion | Bounded everywhere: page size 200, analytics `topN` 500, 3 dimensions and 4 measures, 20 filter conditions, ranges of at most 31, 366 or 36 months depending on the feature, stored bodies 64 KiB, 3 active exports per user, 5 million rows per export. |
| Path traversal through an export download | File names are derived from the job id and checked; a name outside the export directory is refused. |
| Formula injection through exported data | CSV text that starts with `=`, `+`, `-`, `@`, tab or CR gets a leading apostrophe; XLSX text stays text. |
| Information leaks in errors | Errors are `application/problem+json` with a stable code and a message; a crash is a 500 with no stack trace or SQL. |

## 2. What the guard triggers enforce

| Table | Rule |
|---|---|
| `AuditLogEntries` | no delete; an update may only set the hash columns, once; every other column never changes |
| `ApiRequestLogs`, `ApiMutationIntents` | the same: no delete, hash set once, content never changes |
| `ApiRequestBodies`, `TimelineEvents` | append-only: no update, no delete |

They are created by `CreateChronoAuditAuditGuard` and `CreateChronoAuditStoreGuards` in your migration. They do **not** guard `TRUNCATE`, `DROP` or DDL by a privileged user: that is what the HMAC chain and the anchors are for, and database permissions (below).

## 3. Everything that releases sensitive data is audited first

These actions write an audit row *before* the result is returned; if the row cannot be written the call fails and nothing is released:

`ChronoAudit.PersonalDataRevealed`, `ChronoAudit.ActivityBodyViewed`, `ChronoAudit.ExportDownloaded`, `ChronoAudit.ActivityExported`, `ChronoAudit.ExtractRun` (and `ExtractFinished` at the end), `ChronoAudit.EvidenceIssued`, `ChronoAudit.ChainVerified`. Export lifecycle and key rotation are recorded too (`ExportStarted`, `ExportFinished`, `ExportCancelled`, `KeyRotated`). Search the audit table for the `ChronoAudit.` prefix to review who looked at what.

## 4. Authorization

- Every route is behind a policy that **you** name; `MapChronoAudit("")` throws. There is no unauthenticated mode.
- The data scope comes from `IChronoAuditAccessScope`. The default trusts the claim `chronoaudit_scope=all` for All and a `group` claim for Group. **Issue those claims only to people who should have them**, or replace the service with your own authorization logic.
- Anything that needs the All scope (history, audit search and verify, request bodies, extract, evidence, incomplete intents) answers `403 ChronoAudit.ScopeDenied` or `404` to anyone else.

## 5. The key

- At least 32 bytes, from the environment or a secret manager, never from a file in the repository. The development key is public and is refused outside Development and Testing; so is a key whose SHA-256 is in `RefusedKeySha256`.
- The key never appears in logs or in the database, only an 8-character fingerprint.
- Whoever has the key can forge a valid chain. Treat it like a signing key: limit who can read it, keep a copy outside the server, rotate it by the procedure in the [hash chain guide](hash-chain-and-anchors.md).

## 6. Database permissions (recommended)

The triggers are a second line. The first is the account your application uses:

- The application account needs `SELECT` and `INSERT` on the audit and store tables, `UPDATE` where the library updates (the hash columns of the audit, activity and intent tables; the export jobs; saved reports) and `DELETE` only on `SavedReports` (users delete their own reports). It needs no `DELETE` on the audit, activity, body, intent or timeline tables, and no `ALTER`, `DROP`, `TRUNCATE` or trigger rights.
- Apply migrations with a different, privileged account that the application does not hold.
- Put the anchor directory somewhere the application can append to but a database administrator cannot silently rewrite (write-once storage, another host), and copy it off the server regularly.

## 7. What ChronoAudit does not do

- It does not stop a person who holds **both** the database credentials and the HMAC key and also controls the anchor storage; at that point they own the evidence.
- It does not encrypt data at rest; use your database's encryption.
- It does not authenticate users; it consumes your authentication.
- It cannot know which of your fields are personal; you list them.
- The chain proves integrity from the moment a row is hashed. A row is *pending* (unhashed) for a few seconds after it is written; during that time only the triggers protect it.

## 8. Reporting a vulnerability

Please do not open a public issue for a security problem. Use the "Report a vulnerability" button on the Security tab of the GitHub repository (private vulnerability reporting).
