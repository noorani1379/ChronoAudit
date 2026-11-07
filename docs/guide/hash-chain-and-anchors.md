# Hash chain, key rotation and anchors

The database triggers stop an `UPDATE` or `DELETE` of an audit row. The hash chain and the anchors catch what the triggers cannot: someone with enough rights who disables a trigger, edits rows directly and puts them back, or deletes the newest rows.

## 1. The chain

Every audit row, API activity row and mutation intent gets two columns after it is saved: the `PayloadHash` of its own fields and the `PreviousHash` of the row before it. The hash is **HMAC-SHA256** with your secret key over a fixed, culture-independent byte layout of the row's fields and the previous hash. Change one field of one old row and its hash no longer matches, and every later row's `PreviousHash` no longer links; without the key nobody can produce a matching chain.

There is one chain per audited module (its `AuditLogEntries` table) and two for the store: the API activity log (`chronoaudit-activity`) and the mutation intents (`chronoaudit-intents`). Rows are ordered by a `ChainIndex` that the worker assigns, not by the identity column.

**Hashing is done in the background.** Your transaction inserts the row with no hash and commits at once, so no lock slows your writes. `AddHashWorkers()` adds the hosted services that add pending rows to each chain: the audit chains in batches of 500, woken the moment a save happens (and retried after 5 seconds on error); the activity and intent chains every `ChronoAudit:ActivityLog:HashIntervalSeconds` (default 5) in batches of `HashBatchSize` (default 500). A row is *pending* until then. The layout of the hashed fields is pinned by tests and will never change, because that would invalidate every existing chain.

Run the hash workers on **one node** of a multi-instance deployment; if two run, they take a database lock (`sp_getapplock` on SQL Server, an advisory lock on PostgreSQL) and take turns.

**A row that commits late.** Transaction A may get its sequence number before B but commit after it. The worker never waits for A: it hashes B now, and A joins the end of the chain when it commits. That is why the chain is ordered by `ChainIndex` and not by sequence number, and why verification stays valid in this case (proven on real SQL Server and PostgreSQL).

**What the database enforces.** The guard triggers ([getting started](getting-started.md)) allow the hash columns to be set exactly once (from empty to a value) and refuse every other `UPDATE` and every `DELETE` of audit rows, request bodies and timeline events. They do not guard `TRUNCATE` or DDL.

## 2. Verifying

- `POST /chronoaudit/audit/verify` with `{ "module": "shop" }` (All scope) re-walks a chain and answers `valid`, `verifiedCount`, `pendingCount`, the first broken chain index and the kind of failure. For a long chain pass `afterChainIndex` and `maxRows` and continue from `nextAfterChainIndex`. Every run is recorded in the audit trail first. Use `"chronoaudit-activity"` or `"chronoaudit-intents"` as the module to verify the store chains.
- In code: `IAuditChainSources.Find(module)?.VerifyAsync(ct)` or `VerifyRangeAsync(afterChainIndex, maxRows, ct)`.
- `AuditAnchor` worker (below) verifies the newest `VerifyWindowRows` rows of every chain on its schedule.

## 3. The key

The secret is `ChronoAudit:HmacKey` (environment variable `ChronoAudit__HmacKey`): the UTF-8 text of the value, **at least 32 bytes**. Outside Development and Testing a missing, short or published key stops the host at startup; the error never prints the key. Keep a safe copy outside the server: **if the key is lost, old rows can no longer be verified.** The key is never written to the database; rows only record a short fingerprint of the key that signed them (safe to log).

### Rotating the key

Rotation starts a new *key segment* in the chain. Old rows keep verifying with the old key; new rows are hashed with the new key. The order matters:

1. Put the **old** key in `ChronoAudit:PreviousHmacKeys` (a list; every earlier key stays there forever) and the **new** key in `ChronoAudit:HmacKey`. A key in `PreviousHmacKeys` must be a real key (never the development key).
   ```bash
   export ChronoAudit__HmacKey="<the new key, 32+ bytes>"
   export ChronoAudit__PreviousHmacKeys__0="<the old key>"
   ```
2. Deploy. Nothing is re-hashed yet; verification already works because both keys are known.
3. For each chain, start the new segment once, from a maintenance endpoint or a one-off command of your own that calls `IChainMaintenance`:
   ```csharp
   using ChronoAudit.EntityFrameworkCore;

   public sealed class KeyRotation(IChainMaintenance maintenance)
   {
       public async Task RotateAsync(CancellationToken ct)
       {
           foreach (var module in new[] { "library", "chronoaudit-activity", "chronoaudit-intents" })
           {
               var result = await maintenance.RotateKeyAsync(module, ct);
               if (result.IsFailure)
               {
                   Console.WriteLine($"{module}: {result.Error.Code}");
               }
           }
       }
   }
   ```
   This adds a `ChronoAudit.KeyRotated` marker row; the hash worker hashes everything after the marker with the current key. Results: `Chain.UnknownModule`; `Chain.NothingToRotate` (the chain is empty or already on the current key); `Chain.RotationPending` (a rotation is still waiting to be hashed, try again later); `Chain.UnknownKey` (the key that signed the newest row is not in `PreviousHmacKeys`: add it).
4. Verify the chain again.

Never remove a key from `PreviousHmacKeys` while rows signed by it exist.

## 4. Anchors: detecting a removed tail

If someone deletes the **newest** rows of a chain, the rest of the chain is still perfectly valid. Only a copy of the chain head kept somewhere else can reveal it. `AddAnchorWorker()` appends the head of every chain (module, `ChainIndex`, `PayloadHash`, time) as one line to a file in `ChronoAudit:Anchors:Directory` every `IntervalHours`. Each line carries an HMAC that covers the previous line's HMAC, so editing, cutting or inserting a line in the file is itself detected.

| Option (`ChronoAudit:Anchors:`) | Default | Meaning |
|---|---|---|
| `Directory` | `App_Data/chronoaudit-anchors` | where the anchor file is written (relative to the application's base directory) |
| `IntervalHours` | 24 | at least 1 |
| `StartupDelaySeconds` | 60 | wait after start so the first hashing pass can finish |
| `VerifyWindowRows` | 10000 | how many of the newest rows of each chain are re-verified in each round |

The worker compares the anchors with the database every round and logs at **Critical** level when
- the chain is broken (`Audit hash chain is broken: Module ..., first broken chain index ...`),
- anchors no longer match the database (`Audit anchors no longer match the database: ...`, rows missing or changed),
- the anchor file itself has tampered lines (`Audit anchor file has N edited, cut or inserted lines`).

Alert on these messages. **Copy the anchor directory off the server** (a write-once bucket, another host): whoever owns the server owns both the database and a file that sits next to it. A failed write is logged and tried again next round; it never crashes the application. `POST audit/verify` also reports `anchorsChecked` and `anchorMismatches`.
