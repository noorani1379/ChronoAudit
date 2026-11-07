# Configuration reference

Every setting of ChronoAudit, its default, and where it can be set. Defaults are neutral and in English; the values that are written into rows (system user name, calendar) should be chosen **before the first row exists**.

Three ways to set a value, later ones win:

1. `appsettings.json` / environment variables: `ChronoAudit:<Section>:<Name>` (environment: `ChronoAudit__<Section>__<Name>`).
2. Code: the `configure` callback of `AddChronoAudit(configuration, environment, o => { ... })`, or `AddChronoAuditAspNetCore(o => { ... })`.
3. Registering your own service before the library's (the `TryAdd` registrations yield to yours): `IAuditActor`, `ICorrelationContext`, `IChronoAuditAccessScope`, `IPersonalDataRevealPolicy`, `TimeProvider`, `IChronoAuditCalendar`.

Values that are not valid stop the host at startup with a message naming the setting. Settings marked *code only* have no configuration key.

## The key

| Setting | Default | Meaning |
|---|---|---|
| `ChronoAudit:HmacKey` | none | The secret of the hash chains: UTF-8 text, at least 32 bytes. Required outside Development and Testing. Set it by environment variable. |
| `ChronoAudit:PreviousHmacKeys:0`, `:1`, ... | none | Keys of earlier key segments, needed to verify old rows after a rotation. Never the development key. |
| `RefusedKeySha256` (*code only*) | empty | Hex SHA-256 of keys that must be refused outside Development (keys that were ever published). |

## `ChronoAudit:Audit`: audit capture

| Name | Default | Meaning |
|---|---|---|
| `SystemUserId` | `System` | the actor id stamped when nobody is signed in (seed, background job) |
| `SystemDisplayName` | `System` | the display name for it |
| `DelegatedDisplayNameFormat` | `{0} (on behalf)` | the display name when an action is delegated; must contain `{0}` |
| `BacklogWarnRows` | 1000 | the hash worker warns when a module has more unhashed rows |

## `ChronoAudit:Anchors`: external chain heads

| Name | Default | Meaning |
|---|---|---|
| `Directory` | `App_Data/chronoaudit-anchors` | where the anchor file is written (relative to the application base directory) |
| `IntervalHours` | 24 | at least 1 |
| `StartupDelaySeconds` | 60 | wait after start before the first round |
| `VerifyWindowRows` | 10000 | newest rows per chain re-verified in each round |

## `ChronoAudit:Store`, `ChronoAudit:Calendar`

| Name | Default | Meaning |
|---|---|---|
| `Store:Schema` | `chronoaudit` | the schema of the store tables, for the activity log, reports, exports and timeline |
| `Calendar:Kind` | `Gregorian` | `Gregorian` or `Persian`; decides the calendar columns on rows and the analytics time grains |
| `Calendar:TimeZoneId` | `UTC` | the time zone whose midnight starts a "day" |

You can also pass your own `IChronoAuditCalendar` to the context.

## `ChronoAudit:ActivityLog` and `ChronoAudit:Masking`

| Name | Default | Meaning |
|---|---|---|
| `ActivityLog:ExcludedPathPrefixes` | `/health`, `/version`, `/swagger`, `/hubs` | calls under these paths are not logged; setting a list replaces the defaults |
| `ActivityLog:HashIntervalSeconds` | 5 | seconds between rounds of the activity and intent hash worker |
| `ActivityLog:HashBatchSize` | 500 | rows per hash transaction |
| `ActivityLog:IncompleteIntentAgeMinutes` | 5 | an intent without a result older than this is reported as incomplete |
| `Masking:MaxBodyBytes` | 65536 | the largest body stored; a longer one is cut and marked truncated |
| `Masking:CodeIsSecretPathFragments` | `/auth/`, `/otp/`, `/sessions/` | under these paths the field `code` is masked; setting a list replaces the defaults |
| `Masking.SecretNames`, `SecretNameContains`, `SecretNameSuffixes` (*code only*) | see [activity log](activity-log.md) | the names whose values are masked |

## `ChronoAudit:PersonalData`

| Name | Default | Meaning |
|---|---|---|
| `NumberFields` | empty | fields masked as numbers (first four and last two characters stay) |
| `TextFields` | empty | fields masked as `***` |

## `ChronoAudit:Exports`

| Name | Default | Valid | Meaning |
|---|---|---|---|
| `Directory` | `App_Data/chronoaudit-exports` | not empty | where files are written; shared storage for several nodes |
| `RetentionDays` | 7 | 1 to 365 | how long finished files are kept |
| `LeaseSeconds` | 120 | 30 to 3600 | how long a claim lasts without a checkpoint |
| `PollIntervalSeconds` | 5 | 1 to 300 | the pause when the queue is empty |
| `MaxRows` | 5000000 | at least 1 | the most rows an export may hold |
| `MaxActivePerUser` | 3 | 1 to 50 | `Pending` plus `Running` jobs per user |
| `BatchSize` | 1000 | 100 to 5000 | rows per producer batch |

## `ChronoAudit:DataLag`, `ChronoAudit:Consistency`

| Name | Default | Valid | Meaning |
|---|---|---|---|
| `DataLag:CacheSeconds` | 3 | 0 to 60 | how long the answer of your lag sources is remembered |
| `Consistency:IntervalHours` | 6 | 1 to 168 | hours between runs of your consistency checks |
| `Consistency:GraceSeconds` | 60 | 0 to 3600 | wait before a drifting check is run again |

## `ChronoAudit:AspNetCore`

These are read only by `AddChronoAuditAspNetCore(configuration)`; the overload without arguments uses the defaults, and `AddChronoAuditAspNetCore(o => ...)` sets them in code.

| Name | Default | Meaning |
|---|---|---|
| `UserIdClaimTypes` | `sub`, `nameidentifier` | the claims that hold the user id, first match wins |
| `NameClaimTypes` | `name`, `ClaimTypes.Name` | the claims that hold the display name |
| `RoleClaimTypes` | `role`, `ClaimTypes.Role` | the claims that hold the role |
| `GroupIdClaimType` | `group` | the claim whose presence gives the Group scope |
| `SessionIdClaimType` | `sid` | the session id |
| `ActAsUserIdClaimType` | `act_as` | the user on whose behalf the caller acts (delegation) |
| `ScopeClaimType` | `chronoaudit_scope` | the claim with value `all` that gives the All scope |
| `CorrelationHeader` | `X-Correlation-Id` | the header that carries the correlation id (must be a GUID) |
| `DescribeError` (*code only*) | the error message | a `Func<ChronoAuditError, string>` to put the error text in your language |

## An example `appsettings.json`

```json
{
  "ChronoAudit": {
    "Audit": { "SystemUserId": "System", "BacklogWarnRows": 1000 },
    "Anchors": { "Directory": "/var/chronoaudit/anchors", "IntervalHours": 6 },
    "Store": { "Schema": "chronoaudit" },
    "Calendar": { "Kind": "Gregorian", "TimeZoneId": "UTC" },
    "ActivityLog": { "ExcludedPathPrefixes": [ "/health", "/swagger" ] },
    "Masking": { "MaxBodyBytes": 65536 },
    "PersonalData": { "NumberFields": [ "PhoneNumber" ], "TextFields": [ "FullName", "Email" ] },
    "Exports": { "Directory": "/var/chronoaudit/exports", "RetentionDays": 14 },
    "DataLag": { "CacheSeconds": 3 },
    "Consistency": { "IntervalHours": 6, "GraceSeconds": 60 },
    "AspNetCore": { "GroupIdClaimType": "department" }
  }
}
```

The key is not in this file: `ChronoAudit__HmacKey` and `ChronoAudit__PreviousHmacKeys__0` come from the environment or a secret manager.
