using System.Globalization;
using System.Text.Json.Nodes;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

/// <summary>An entity whose key the real database generates: its audit row carries the real key, and the history finds the record by it.</summary>
public abstract class GeneratedKeyTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyTheAuditRow_CarriesTheKeyTheDatabaseMadeAsync()
    {
        var db = Fixture.Database;
        var ids = new List<int>();
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            var tickets = new[] { new Ticket { Title = "one" }, new Ticket { Title = "two" }, new Ticket { Title = "three" } };
            shop.Tickets.AddRange(tickets);
            await shop.SaveChangesAsync();
            ids.AddRange(tickets.Select(t => t.Id));
        }

        Assert.All(ids, id => Assert.True(id > 0, "the key must be the one the database generated"));

        using var read = db.Scope();
        var rows = await db.Shop(read).AuditLogEntries.AsNoTracking()
            .Where(a => a.ResourceType == "Tickets").ToListAsync();
        var keys = rows.Select(r => JsonNode.Parse(r.ResourceId)!["Id"]!.GetValue<int>()).OrderBy(k => k).ToList();
        Assert.Equal(ids.OrderBy(k => k), keys);
        Assert.All(rows, r => Assert.Equal(AuditActions.AutoAdded, r.Action));

        // The history of a new record is found by its real key and starts with its creation.
        var changes = await read.ServiceProvider.GetRequiredService<IChangeReader>().RecordAsync(
            "shop", "Tickets", ids[0].ToString(CultureInfo.InvariantCulture), ChronoAuditScope.Everything("admin"), null, 50, false, CancellationToken.None);
        Assert.True(changes.IsSuccess, changes.IsFailure ? changes.Error.Code : null);
        Assert.Equal(AuditActions.AutoAdded, Assert.Single(changes.Value.Items).Action);
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerGeneratedKeyTests(SqlServerDatabaseFixture fixture)
    : GeneratedKeyTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task TheAuditRow_CarriesTheKeyTheDatabaseMade() => VerifyTheAuditRow_CarriesTheKeyTheDatabaseMadeAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlGeneratedKeyTests(PostgreSqlDatabaseFixture fixture)
    : GeneratedKeyTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task TheAuditRow_CarriesTheKeyTheDatabaseMade() => VerifyTheAuditRow_CarriesTheKeyTheDatabaseMadeAsync();
}
