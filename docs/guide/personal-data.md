# Personal data and secrets

ChronoAudit stores what changed, so it would store a phone number or a password just as readily. Two different mechanisms keep them out of sight, and it matters which one you use:

| | Secret (`[SensitiveData]`) | Personal field (`ChronoAudit:PersonalData`) |
|---|---|---|
| Stored in the audit row | `***MASKED***`; the real value is never kept | the real value |
| Who can see the real value | nobody, ever | a caller whom your `IPersonalDataRevealPolicy` allows, and the reveal is audited |
| Use it for | passwords, tokens, card numbers, anything nobody needs again | phone numbers, national ids, names, addresses, emails |

## 1. Secrets: `[SensitiveData]`

Put the attribute on a property and its old and new values are written as `***MASKED***` in every audit row. The column is also flagged as secret in the table catalog. The change is still visible (the field changed), the value is not.

```csharp
using ChronoAudit.EntityFrameworkCore;

public sealed class MemberContact : AuditableEntityBase
{
    public int MemberId { get; set; }

    [SensitiveData]
    public required string PhoneNumber { get; set; }

    public required string Address { get; set; }
}
```

## 2. Personal fields: masked when read

List the field names (case-insensitive, matched on any audited table) in the configuration. They are stored whole and masked on the way out.

```json
{
  "ChronoAudit": {
    "PersonalData": {
      "NumberFields": [ "PhoneNumber", "NationalId" ],
      "TextFields": [ "FullName", "Email", "Address" ]
    }
  }
}
```

Both lists are empty by default; ChronoAudit does not guess. Persian and Arabic-Indic digits in a number field are converted to ASCII digits before masking.

| List | Mask | Example |
|---|---|---|
| `NumberFields` | keeps the first four and last two characters, stars in between; a value of six characters or less becomes all stars | `09123456789` becomes `0912*****89` |
| `TextFields` | the fixed text `***` | `Frank Herbert` becomes `***` |

Where the masking applies (always, unless a reveal was granted):

- the field changes returned by `IChangeReader` and `GET /chronoaudit/history/...`,
- the payload of timeline events, the evidence bundle and the timeline export,
- the audit extract (`GET /chronoaudit/extract/csv`), which is never revealed.

Where it does *not* apply: raw rows from `IAuditSearch` (a server-side API for the All scope; use `[SensitiveData]` for values that must never reach it) and your own tables. Request bodies of the API activity log are masked by a different, name-based rule, see the [activity log guide](activity-log.md).

## 3. The reveal policy

By default nobody may reveal anything. To allow it, register an implementation of `IPersonalDataRevealPolicy`. It receives the caller's `ChronoAuditScope` and the table name, and answers yes or no. Fail closed: when in doubt, return `false`.

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;

public sealed class ComplianceRevealPolicy : IPersonalDataRevealPolicy
{
    public Task<bool> MayRevealAsync(ChronoAuditScope scope, string resourceType, CancellationToken cancellationToken)
    {
        // Only callers with the All scope, and never the payments table.
        var allowed = scope.Kind == ScopeKind.All && resourceType != "Payments";
        return Task.FromResult(allowed);
    }
}

public static class RevealPolicyRegistration
{
    public static void Register(IServiceCollection services)
        => services.AddSingleton<IPersonalDataRevealPolicy, ComplianceRevealPolicy>();
}
```

A caller asks for the real values with `reveal: true` on `IChangeReader`, or `?reveal=true` on the history endpoints. Then:

1. If the policy says no, the call still succeeds, but the page comes back masked and says `PersonalDataRevealed: false`. A refused reveal is not an error: the caller simply gets what an ordinary caller gets.
2. If the policy says yes, ChronoAudit writes an audit row (`AuditActions.PersonalDataRevealed`, resource type = the table, resource id = the record key, metadata = `viewer` and the `fields` that were personal) **before** it returns the values. If that row cannot be saved, the failure is logged as a warning and the page again comes back masked: no row, no reveal.
3. Only when the row exists is the page returned whole, with `PersonalDataRevealed: true`.

A page with no personal fields in it is returned as usual and no reveal row is written. One reveal row is written per page that contained personal fields.

## 4. Choosing between them

- If nobody will ever need to read it back: `[SensitiveData]`.
- If a compliance officer may need it, but a support agent must not: a personal field plus a reveal policy.
- If it is neither: leave it alone, the audit trail is more useful whole.
