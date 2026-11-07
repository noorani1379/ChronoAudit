using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The database providers ChronoAudit knows by name.</summary>
internal enum ProviderKind
{
    Unknown = 0,
    SqlServer,
    PostgreSql,
    Sqlite,
    InMemory,
}

/// <summary>
/// Everything that differs between database providers, chosen at run time from the provider name, so ChronoAudit never references a
/// provider package (PLAN D1, D9). This folder is the only place in <c>src/</c> that may contain SQL text.
/// </summary>
internal sealed class ChronoAuditDialect
{
    public const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    public const string PostgreSqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    public const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    public const string InMemoryProvider = "Microsoft.EntityFrameworkCore.InMemory";

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Properties = new();

    private static readonly ChronoAuditDialect SqlServer = new(ProviderKind.SqlServer);
    private static readonly ChronoAuditDialect PostgreSql = new(ProviderKind.PostgreSql);
    private static readonly ChronoAuditDialect Sqlite = new(ProviderKind.Sqlite);
    private static readonly ChronoAuditDialect InMemory = new(ProviderKind.InMemory);
    private static readonly ChronoAuditDialect Unrecognized = new(ProviderKind.Unknown);

    internal ChronoAuditDialect(ProviderKind kind) => Kind = kind;

    public ProviderKind Kind { get; }

    public static ChronoAuditDialect For(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return For(db.Database.ProviderName);
    }

    public static ChronoAuditDialect For(string? providerName) => providerName switch
    {
        SqlServerProvider => SqlServer,
        PostgreSqlProvider => PostgreSql,
        SqliteProvider => Sqlite,
        InMemoryProvider => InMemory,
        _ => Unrecognized,
    };

    /// <summary>A column name quoted for this provider: <c>[Col]</c> for SQL Server, <c>"Col"</c> for the others.</summary>
    public string Quote(string column) => Kind == ProviderKind.SqlServer ? "[" + column + "]" : "\"" + column + "\"";

    public string IsNull(string column) => Quote(column) + " IS NULL";

    public string IsNotNull(string column) => Quote(column) + " IS NOT NULL";

    /// <summary>
    /// Takes the transaction-scoped lock that serializes the writers of one chain (SQL Server <c>sp_getapplock</c>, PostgreSQL advisory lock).
    /// It is released at commit, so the transaction must be open. SQLite, EF InMemory and unknown providers take no lock.
    /// </summary>
    public async Task AcquireChainLockAsync(DbContext db, string lockName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        if (Kind is not (ProviderKind.SqlServer or ProviderKind.PostgreSql))
        {
            return;
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The chain lock needs an open transaction.");
        }

        if (Kind == ProviderKind.SqlServer)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {lockName}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; IF @r < 0 THROW 51001, 'ChronoAudit chain lock was not granted.', 1;",
                cancellationToken);
            return;
        }

        // The text is a constant, so raw SQL is safe here.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '30s'", cancellationToken);
        var key = LockKey(lockName);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    /// <summary>The 64-bit advisory lock key of a lock name: the first 8 bytes of its SHA-256.</summary>
    public static long LockKey(string lockName) => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(lockName)));

    /// <summary>True when the exception (or an inner one) is a unique-constraint violation.</summary>
    public bool IsUniqueViolation(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is DbException { SqlState: "23505" })
            {
                return true;
            }

            var type = ex.GetType().FullName;
            if (type == "Microsoft.Data.SqlClient.SqlException" && Number(ex, "Number") is 2601 or 2627)
            {
                return true;
            }

            if (type == "Microsoft.Data.Sqlite.SqliteException"
                && Number(ex, "SqliteErrorCode") == 19 && Number(ex, "SqliteExtendedErrorCode") is 2067 or 1555)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for errors a retry can fix: deadlocks, timeouts, failovers.</summary>
    public bool IsTransient(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is TimeoutException)
            {
                return true;
            }

            if (ex is DbException db && (db.IsTransient || db.SqlState is "40001" or "40P01"))
            {
                return true;
            }

            if (ex.GetType().FullName == "Microsoft.Data.SqlClient.SqlException" && Number(ex, "Number") is 1205 or -2 or 40613 or 40501 or 49918 or 49919 or 49920)
            {
                return true;
            }
        }

        return false;
    }

    // Provider exceptions are read through cached reflection, so no provider assembly is referenced.
    private static int? Number(Exception exception, string property)
    {
        var info = Properties.GetOrAdd((exception.GetType(), property), static key => key.Item1.GetProperty(key.Item2));
        return info?.GetValue(exception) is int value ? value : null;
    }
}
