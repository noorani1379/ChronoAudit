using ChronoAudit.EntityFrameworkCore;

namespace Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Migration helpers that create the database triggers which make the ChronoAudit tables tamper-resistant. Call them from a migration of
/// the host. The triggers do not guard <c>TRUNCATE</c> or DDL; the anchors (see <see cref="ChronoAudit.EntityFrameworkCore.AuditAnchorLog"/>) detect a removed tail.
/// </summary>
public static class ChronoAuditMigrationBuilderExtensions
{
    /// <summary>Guards the <c>AuditLogEntries</c> table of an audited context: no delete; an update only sets the first hash and chain index.</summary>
    /// <param name="migrationBuilder">The migration builder.</param>
    /// <param name="schema">The schema of the table; null means the provider default (<c>dbo</c>, <c>public</c>; ignored by SQLite).</param>
    /// <returns>The migration builder.</returns>
    /// <exception cref="NotSupportedException">The provider is not SQL Server, PostgreSQL or SQLite.</exception>
    public static MigrationBuilder CreateChronoAuditAuditGuard(this MigrationBuilder migrationBuilder, string? schema = null)
        => Apply(migrationBuilder, GuardSql.Create, schema, GuardTable.Audit);

    /// <summary>Removes the guard of <c>AuditLogEntries</c>.</summary>
    /// <param name="migrationBuilder">The migration builder.</param>
    /// <param name="schema">The schema of the table; null means the provider default.</param>
    /// <returns>The migration builder.</returns>
    public static MigrationBuilder DropChronoAuditAuditGuard(this MigrationBuilder migrationBuilder, string? schema = null)
        => Apply(migrationBuilder, GuardSql.Drop, schema, GuardTable.Audit);

    /// <summary>Guards the store tables: the request log and the mutation intents (hash once), the request bodies and the timeline events (append-only).</summary>
    /// <param name="migrationBuilder">The migration builder.</param>
    /// <param name="schema">The schema of the store tables (default <c>chronoaudit</c>).</param>
    /// <returns>The migration builder.</returns>
    public static MigrationBuilder CreateChronoAuditStoreGuards(this MigrationBuilder migrationBuilder, string schema = "chronoaudit")
    {
        foreach (var table in StoreTables)
        {
            Apply(migrationBuilder, GuardSql.Create, StoreSchema(migrationBuilder, schema), table);
        }

        return migrationBuilder;
    }

    /// <summary>Removes the guards of the store tables.</summary>
    /// <param name="migrationBuilder">The migration builder.</param>
    /// <param name="schema">The schema of the store tables (default <c>chronoaudit</c>).</param>
    /// <returns>The migration builder.</returns>
    public static MigrationBuilder DropChronoAuditStoreGuards(this MigrationBuilder migrationBuilder, string schema = "chronoaudit")
    {
        foreach (var table in StoreTables)
        {
            Apply(migrationBuilder, GuardSql.Drop, StoreSchema(migrationBuilder, schema), table);
        }

        return migrationBuilder;
    }

    private static GuardTable[] StoreTables => [GuardTable.RequestLogs, GuardTable.Intents, GuardTable.Bodies, GuardTable.Timeline];

    // SQLite has no schemas: the store tables live in the main database.
    private static string? StoreSchema(MigrationBuilder migrationBuilder, string schema)
        => migrationBuilder.ActiveProvider == ChronoAuditDialect.SqliteProvider ? null : schema;

    private static MigrationBuilder Apply(
        MigrationBuilder migrationBuilder,
        Func<string?, string?, GuardTable, IReadOnlyList<string>> build,
        string? schema,
        GuardTable table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        foreach (var sql in build(migrationBuilder.ActiveProvider, schema, table))
        {
            migrationBuilder.Sql(sql);
        }

        return migrationBuilder;
    }
}
