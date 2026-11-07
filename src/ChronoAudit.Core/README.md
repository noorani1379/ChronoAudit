# ChronoAudit.Core

The dependency-free building blocks of [ChronoAudit](https://github.com/noorani1379/ChronoAudit): the result type, access scopes, the analytics query model, masking, the HMAC field hasher, calendars and the CSV and XLSX writers. It references no database and no web framework, so you can use it on its own or from your own library. Most applications install `ChronoAudit.EntityFrameworkCore` and `ChronoAudit.AspNetCore` instead; both bring this package.

## What is in it

- `ChronoAuditResult` / `ChronoAuditResult<T>` and `ChronoAuditError`: predictable failures as values, with a stable code and a kind (validation, not found, forbidden, conflict, ...).
- `ChronoAuditScope` and `ScopeKind` (All, Group, Own): who may see which rows.
- `AnalyticsQuery`, `FilterNode`, `AnalyticsSubject<TRow>`: the closed-catalog analytics model that becomes one SQL statement.
- `PersonalDataMasker`, `SensitiveDataMasker`, `BodyMasker`, `MaskingOptions`: masking of secrets and personal data.
- `ChronoAuditKey` and `FieldChainHasher`: the HMAC key rules and the canonical row hashing.
- `IChronoAuditCalendar` (Gregorian and Persian) and `PersianTextNormalizer`.
- `CsvWriter` and `XlsxWriter`: streaming, formula-safe CSV and a dependency-free OpenXML spreadsheet writer.
- `ChronoAuditMetrics`: the name of the metrics meter (`ChronoAudit`).

## Example

```csharp
using ChronoAudit;

public static class CoreExample
{
    public static string Run()
    {
        ChronoAuditResult<int> parsed = int.TryParse("42", out var number)
            ? ChronoAuditResult.Success(number)
            : ChronoAuditError.Validation("Example.NotANumber", "Not a number.");

        var phone = PersonalDataMasker.MaskNumber("09123456789");   // 0912*****89

        return parsed.IsSuccess ? $"{parsed.Value} {phone}" : parsed.Error.Code;
    }
}
```

Documentation: [github.com/noorani1379/ChronoAudit](https://github.com/noorani1379/ChronoAudit).
