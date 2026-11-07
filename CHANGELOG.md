# Changelog

All notable changes to ChronoAudit are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0 the public API may change between previews.

## [0.1.0-preview.1] - 2026-11-03

First public preview.

Before this release the sample was run against a real SQL Server and fixed (migrations, audit of generated keys, evidence for non-Guid keys); none of that was ever published.

### Added

- **`ChronoAudit.Core`**: `ChronoAuditResult` / `ChronoAuditError` (failures as values with stable codes), access scopes (`ChronoAuditScope`: All, Group, Own), the analytics query and filter model, masking of secrets and personal data, the HMAC field hasher and key rules, Gregorian and Persian calendars, and streaming `CsvWriter` and `XlsxWriter`.
- **`ChronoAudit.EntityFrameworkCore`**:
  - `AuditableDbContext` and `AuditableEntityBase`: a field-level audit row for every saved change, in the same transaction; soft delete for `IAuditableEntity`.
  - Tamper evidence: HMAC-SHA256 hash chains built by background workers, database guard triggers (SQL Server, PostgreSQL, SQLite), signed anchor files outside the database, key rotation with `PreviousHmacKeys` and `IChainMaintenance`, and chain verification.
  - Reading: `IChangeReader` (history of a table or record, with masked personal data and an audited reveal), `IAuditSearch`, `IExtractReader`.
  - The store: API activity log and mutation intents (hash-chained), saved reports, export jobs with a resumable multi-node worker (producers `activity`, `audit`, `extract`, `analytics`, `timeline`, and your own), the append-only business timeline with idempotent recording, data lag, consistency checks and evidence bundles.
  - Analytics: one-statement grouping with up to 3 dimensions and 4 measures, filters, period comparison, built-in subjects (`audit:{module}`, `activity`, `timeline`) and your own subjects.
- **`ChronoAudit.AspNetCore`**: activity log middleware, mutation intent filters (MVC and minimal APIs), actor, correlation and access scope from the request, the `MapChronoAudit` endpoint groups behind a mandatory authorization policy, `X-Data-Lag-Seconds`, `X-Content-SHA256`, and `application/problem+json` errors.
- **Sample and documentation**: `samples/ChronoAudit.Sample.WebApi` (SQL Server and PostgreSQL migrations, `requests.http`), a consumer smoke test, and the guides in `docs/guide`.

### Verified

- Unit tests on `net8.0` and `net10.0`, and integration tests against real SQL Server and PostgreSQL 17 (guard triggers, chain locking and late commits, export claim races through the real worker, concurrency tokens, analytics translation, unique-violation and deadlock detection).
- A scale measurement on 1,000,000 rows on both databases, see [`docs/guide/performance.md`](docs/guide/performance.md).

### Known limits

- SQLite is for tests and demos: its triggers are weaker and EF Core 8's SQLite provider cannot aggregate or order `decimal`.
- The mutation intent hashes the bytes the request had read when the filter ran; an endpoint that reads the raw body itself after the filter gets a hash of the part read before.
- Per-request overhead has not been measured.
