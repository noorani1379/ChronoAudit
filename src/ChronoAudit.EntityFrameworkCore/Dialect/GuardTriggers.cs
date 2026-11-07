using System.Globalization;


namespace ChronoAudit.EntityFrameworkCore;

/// <summary>One guarded table: the key column, the columns that may never change and the guard kind.</summary>
internal sealed record GuardTable(string Table, string Key, string[] ImmutableColumns, bool HashOnce)
{
    public static readonly GuardTable Audit = new(
        "AuditLogEntries",
        "SequenceNumber",
        ["Id", "CorrelationId", "ActorUserId", "Action", "ResourceType", "ResourceId", "OldValues", "NewValues", "Metadata", "OccurredAtUtc", "CalendarDate"],
        HashOnce: true);

    public static readonly GuardTable RequestLogs = new(
        "ApiRequestLogs",
        "Id",
        [
            "RequestId", "OccurredAtUtc", "DurationMs", "UserId", "ActAsUserId", "SessionId", "CorrelationId", "CalendarYear", "CalendarMonth", "CalendarDay",
            "CalendarQuarter", "HttpMethod", "Path", "EndpointKey", "QueryString", "StatusCode", "ErrorCode", "IpAddress", "UserAgent", "HasBody",
        ],
        HashOnce: true);

    public static readonly GuardTable Intents = new(
        "ApiMutationIntents",
        "Id",
        ["RequestId", "StartedAtUtc", "UserId", "ActAsUserId", "SessionId", "CorrelationId", "Method", "Path", "EndpointKey", "BodySha256", "BodyLength"],
        HashOnce: true);

    public static readonly GuardTable Bodies = new("ApiRequestBodies", "ApiRequestLogId", [], HashOnce: false);

    public static readonly GuardTable Timeline = new("TimelineEvents", "Id", [], HashOnce: false);
}

/// <summary>Builds the guard trigger statements per provider. Identifiers come only from the constants of <see cref="GuardTable"/> and the schema.</summary>
internal static class GuardSql
{
    public static IReadOnlyList<string> Create(string? provider, string? schema, GuardTable table)
    {
        var t = table.Table;
        switch (provider)
        {
            case ChronoAuditDialect.SqlServerProvider:
            {
                var s = schema ?? "dbo";
                if (!table.HashOnce)
                {
                    return [string.Create(CultureInfo.InvariantCulture, $"CREATE OR ALTER TRIGGER [{s}].[TR_{t}_Guard] ON [{s}].[{t}] AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51000, N'ChronoAudit: rows of {t} are append-only.', 1; END")];
                }

                var cols = string.Join(", ", table.ImmutableColumns.Select(c => $"d.[{c}]"));
                var icols = string.Join(", ", table.ImmutableColumns.Select(c => $"i.[{c}]"));
                var k = table.Key;
                return
                [
                    $"CREATE OR ALTER TRIGGER [{s}].[TR_{t}_Guard] ON [{s}].[{t}] AFTER UPDATE, DELETE AS\n"
                    + "BEGIN\n"
                    + "    SET NOCOUNT ON;\n"
                    + "    IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)\n"
                    + $"        THROW 51000, N'ChronoAudit: rows of {t} cannot be deleted.', 1;\n"
                    + $"    IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[{k}] = d.[{k}]\n"
                    + $"               WHERE d.[PayloadHash] IS NOT NULL OR EXISTS (SELECT {cols} EXCEPT SELECT {icols}))\n"
                    + $"        THROW 51000, N'ChronoAudit: rows of {t} are append-only; only the first hash may be set.', 1;\n"
                    + "END",
                ];
            }

            case ChronoAuditDialect.PostgreSqlProvider:
            {
                var s = schema ?? "public";
                var body = table.HashOnce
                    ? "  IF TG_OP = 'DELETE' THEN\n"
                      + $"    RAISE EXCEPTION 'ChronoAudit: rows of {t} cannot be deleted.' USING ERRCODE = 'restrict_violation';\n"
                      + "  END IF;\n"
                      + $"  IF OLD.\"PayloadHash\" IS NOT NULL OR ROW({string.Join(", ", table.ImmutableColumns.Select(c => $"OLD.\"{c}\""))}) IS DISTINCT FROM ROW({string.Join(", ", table.ImmutableColumns.Select(c => $"NEW.\"{c}\""))}) THEN\n"
                      + $"    RAISE EXCEPTION 'ChronoAudit: rows of {t} are append-only; only the first hash may be set.' USING ERRCODE = 'restrict_violation';\n"
                      + "  END IF;\n"
                      + "  RETURN NEW;\n"
                    : $"  RAISE EXCEPTION 'ChronoAudit: rows of {t} are append-only.' USING ERRCODE = 'restrict_violation';\n";
                return
                [
                    $"CREATE OR REPLACE FUNCTION \"{s}\".\"chronoaudit_guard_{t}\"() RETURNS trigger LANGUAGE plpgsql AS $$\nBEGIN\n{body}END $$",
                    $"DROP TRIGGER IF EXISTS \"TR_{t}_Guard\" ON \"{s}\".\"{t}\"",
                    $"CREATE TRIGGER \"TR_{t}_Guard\" BEFORE UPDATE OR DELETE ON \"{s}\".\"{t}\" FOR EACH ROW EXECUTE FUNCTION \"{s}\".\"chronoaudit_guard_{t}\"()",
                ];
            }

            case ChronoAuditDialect.SqliteProvider:
            {
                var statements = new List<string>
                {
                    $"CREATE TRIGGER IF NOT EXISTS \"TR_{t}_GuardDelete\" BEFORE DELETE ON \"{t}\" BEGIN SELECT RAISE(ABORT, 'ChronoAudit: rows of {t} cannot be deleted.'); END",
                };
                statements.Add(table.HashOnce
                    ? $"CREATE TRIGGER IF NOT EXISTS \"TR_{t}_GuardUpdate\" BEFORE UPDATE ON \"{t}\" WHEN OLD.\"PayloadHash\" IS NOT NULL OR "
                      + string.Join(" OR ", table.ImmutableColumns.Select(c => $"OLD.\"{c}\" IS NOT NEW.\"{c}\""))
                      + $" BEGIN SELECT RAISE(ABORT, 'ChronoAudit: rows of {t} are append-only; only the first hash may be set.'); END"
                    : $"CREATE TRIGGER IF NOT EXISTS \"TR_{t}_GuardUpdate\" BEFORE UPDATE ON \"{t}\" BEGIN SELECT RAISE(ABORT, 'ChronoAudit: rows of {t} are append-only.'); END");
                return statements;
            }

            default:
                throw new NotSupportedException($"ChronoAudit guard triggers are not available for the provider '{provider}'.");
        }
    }

    public static IReadOnlyList<string> Drop(string? provider, string? schema, GuardTable table)
    {
        var t = table.Table;
        switch (provider)
        {
            case ChronoAuditDialect.SqlServerProvider:
                return [$"DROP TRIGGER IF EXISTS [{schema ?? "dbo"}].[TR_{t}_Guard]"];
            case ChronoAuditDialect.PostgreSqlProvider:
            {
                var s = schema ?? "public";
                return [$"DROP TRIGGER IF EXISTS \"TR_{t}_Guard\" ON \"{s}\".\"{t}\"", $"DROP FUNCTION IF EXISTS \"{s}\".\"chronoaudit_guard_{t}\"()"];
            }

            case ChronoAuditDialect.SqliteProvider:
                return [$"DROP TRIGGER IF EXISTS \"TR_{t}_GuardDelete\"", $"DROP TRIGGER IF EXISTS \"TR_{t}_GuardUpdate\""];
            default:
                throw new NotSupportedException($"ChronoAudit guard triggers are not available for the provider '{provider}'.");
        }
    }
}
