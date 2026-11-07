namespace ChronoAudit.AspNetCore.Tests;

/// <summary>A fact that needs SQLite to aggregate and order decimals: EF Core 8's SQLite provider cannot, EF Core 10's can. SQL Server and PostgreSQL are proven in the integration tests (P12).</summary>
internal sealed class SqliteDecimalFactAttribute : FactAttribute
{
    public SqliteDecimalFactAttribute()
    {
#if !NET10_0_OR_GREATER
        Skip = "EF Core 8 SQLite cannot aggregate or order decimals; covered on net10.0 and by the real-database tests.";
#endif
    }
}
