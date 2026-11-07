using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;

namespace ChronoAudit.Sample.WebApi;

/// <summary>
/// The sample ships one migration folder per database provider in the same assembly (<c>Migrations/SqlServer</c>, <c>Migrations/PostgreSql</c>).
/// EF Core would find both and apply them one after the other, so this service shows it only the folder of the provider in use,
/// for the migrations and for the model snapshot. An application with ONE provider needs none of this.
/// </summary>
#pragma warning disable EF1001 // EF Core offers no public extension point for choosing migrations by provider.
public sealed class ProviderMigrationsAssembly : IMigrationsAssembly
{
    private const string Root = "ChronoAudit.Sample.WebApi.Migrations.";

    private readonly MigrationsAssembly _inner;
    private readonly string _namespace;
    private readonly Lazy<ModelSnapshot?> _snapshot;

    /// <summary>Creates the service; EF Core calls this through dependency injection.</summary>
    public ProviderMigrationsAssembly(
        ICurrentDbContext currentContext, IDbContextOptions options, IMigrationsIdGenerator idGenerator, IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
    {
        _inner = new MigrationsAssembly(currentContext, options, idGenerator, logger);
        _namespace = Root + FolderOf(currentContext.Context.Database.ProviderName);
        _snapshot = new Lazy<ModelSnapshot?>(() =>
        {
            var type = _inner.Assembly.GetTypes().FirstOrDefault(t => t.Namespace == _namespace && typeof(ModelSnapshot).IsAssignableFrom(t) && !t.IsAbstract);
            return type is null ? null : (ModelSnapshot?)Activator.CreateInstance(type);
        });
    }

    /// <summary>The folder name of a provider.</summary>
    /// <param name="providerName">The EF Core provider name.</param>
    /// <returns><c>SqlServer</c> or <c>PostgreSql</c>.</returns>
    public static string FolderOf(string? providerName) => providerName switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer" => "SqlServer",
        "Npgsql.EntityFrameworkCore.PostgreSQL" => "PostgreSql",
        _ => throw new NotSupportedException($"The sample has no migrations for the provider '{providerName}'."),
    };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, TypeInfo> Migrations
        => _inner.Migrations.Where(m => m.Value.Namespace == _namespace).ToDictionary(m => m.Key, m => m.Value);

    /// <inheritdoc />
    public ModelSnapshot? ModelSnapshot => _snapshot.Value;

    /// <inheritdoc />
    public Assembly Assembly => _inner.Assembly;

    /// <inheritdoc />
    public string? FindMigrationId(string nameOrId)
        => Migrations.Keys.FirstOrDefault(k => k == nameOrId || k.EndsWith("_" + nameOrId, StringComparison.Ordinal));

    /// <inheritdoc />
    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider) => _inner.CreateMigration(migrationClass, activeProvider);
}
#pragma warning restore EF1001
