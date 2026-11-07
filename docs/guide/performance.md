# Performance

This page separates **how the library is built to stay fast** (design, which you can read in the code) from **what was measured** (numbers, with the machine they came from). Anything not in the measurement table has not been measured; treat it as a design intent, not a promise.

## 1. Design

**Writes (your request path).**

- Audit rows are inserted in the same `SaveChanges` as your change: one extra batched `INSERT` per changed entity, no extra round trip, no lock held for hashing.
- Hashing is done later by a background worker. The request never waits for it.
- The activity middleware writes its row after the response is produced, with its own short transaction; a failing store is logged and never slows or breaks the call.
- A mutation intent costs one small insert before the handler runs, and hashes the body as it is read (no buffering, no copy of binary bodies).

**Interactive reads.**

- Every list is keyset-paged by a unique, indexed key (no `OFFSET`), read with `AsNoTracking` and a projection, with a server-side maximum page size of 200.
- The columns the search filters use are indexed, and filtered indexes keep the "unhashed rows" lookups small.
- Reads use the access scope inside the `WHERE`, so a restricted caller reads fewer rows, not the same rows filtered afterwards.

**Analytics.** One SQL statement per query (a second only for the comparison with the previous period): rows are flattened into a fixed shape, grouped by up to 3 keys and aggregated by up to 4 measures in the database. Result size is bounded by `topN` (500), the range by 36 months.

**Large outputs.** Exports are produced in batches by a background worker with a checkpoint after each batch, a CSV is written straight to the file, an XLSX is spooled and converted, and the activity CSV and extract are streamed to the response; none holds the whole result in memory.

**What costs you.** The audit table grows by one row per changed entity; the activity log by one row per API call. Plan retention (archiving or partitioning by date) yourself; the library never deletes audit rows.

## 2. Measurements

Measured with `tests/ChronoAudit.IntegrationTests/ScaleMeasurement.cs` (run it yourself: set `CHRONOAUDIT_SCALE=1` with the two connection-string variables and run `dotnet test tests/ChronoAudit.IntegrationTests --filter ScaleMeasurement`).

**Machine:** 11th Gen Intel Core i7-11800H @ 2.30 GHz, 24 GB RAM, Windows 11, SQL Server Express and PostgreSQL 17 in Docker Desktop containers on the same machine, default container settings, .NET 10. One run each, single user, no other load.

**Data:** 1,000,000 API activity rows over 30 days, statistics refreshed after the bulk load (`ANALYZE` / `UPDATE STATISTICS`; a running system does this by itself within minutes).

| Operation | SQL Server Express | PostgreSQL 17 |
|---|---|---|
| Bulk load of 1,000,000 rows (raw SQL, for the test) | 20.3 s | 16.2 s |
| Hash a backlog of 5,000 unhashed rows into the chain | 1.95 s | 1.41 s |
| Analytics: 30 days, 2 dimensions, 2 measures, over 1,000,000 rows | 0.70 s | 0.16 s |
| Activity search, one page of 50 (keyset) over 1,000,000 rows | 3 ms | 2 ms |
| Export of 100,000 activity rows to CSV (producer + file) | 1.18 s | 1.21 s |

Things to know when reading the table:

- **Bulk-loaded tables need statistics.** The first PostgreSQL run of this test, made before the statistics step was added, took 445 ms for the search page and 55.7 s for the export: the planner had no statistics for a table that had just been loaded with a million rows. After `ANALYZE` the same queries took 2 ms and 1.2 s. After a large import, run `ANALYZE` (PostgreSQL) or `UPDATE STATISTICS` (SQL Server) before you judge a query.
- These are single-run figures on a development laptop with database containers, not capacity numbers. They say nothing about concurrent load, other hardware or network latency.
- Per-request overhead of `SaveChanges`, the middleware and the intent filter has **not** been measured.
- Hashing speed is limited by the one chain: rows are hashed in order, in batches of 500, by one worker at a time.

## 3. Getting the most out of it

1. **SQL Server: enable `READ_COMMITTED_SNAPSHOT`**, so analytics and searches never block your writers ([operations](operations.md)).
2. **Run the hash worker on one node** and the export workers where you have CPU and disk to spare.
3. **Keep the page size small** in your screens; ask for the next page with the cursor.
4. **Narrow analytics by range and filter first**; the range is required for a reason.
5. **Use pooled connections** (the defaults of `Microsoft.Data.SqlClient` and Npgsql) and size your pool for the web nodes and the workers together.
6. **Archive old rows** you no longer query, but keep the anchors and the key for as long as the rows matter.
