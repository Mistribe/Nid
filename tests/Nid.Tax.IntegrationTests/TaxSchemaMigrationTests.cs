using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Tax.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Tax.IntegrationTests;

/// <summary>
/// #512, step 1 of 2: the tax module takes over the tables Core created, where they are (<c>core</c>), so the previous image still runs
/// on a migrated database (architecture.md §10.1). Step 2 (#568) moves them to the <c>tax</c> schema.
/// </summary>
public class TaxSchemaMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Core's last migration before the tax module left it: the previous image's model.</summary>
    private const string BeforeTaxModule = "20261123073823_AllowAssistantOrigin";

    private static readonly Guid Adult = Guid.Parse("01920000-0000-7000-8000-000000000001");
    private static readonly Guid Notice = Guid.Parse("01920000-0000-7000-8000-0000000000a1");

    private static readonly string[] Tables = ["core.tax_assessment", "core.tax_assessment_declarant", "core.tax_profile"];

    [Fact]
    public async Task An_existing_household_s_profile_and_notices_are_read_through_the_tax_module()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connectionString);
        await core.GetService<IMigrator>().MigrateAsync(BeforeTaxModule, ct);
        await ExecuteAsync(connectionString, $$"""
            insert into core.person (id, kind, display_name, session_epoch, preferences, created_at) values
              ('{{Adult}}', 'adult', 'Alice', 0, '{}', now());
            insert into core.tax_profile (year, data) values (2025, '{"childrenUnder6":1}');
            insert into core.tax_assessment (id, year, data) values ('{{Notice}}', 2024, '{"year":2024}');
            insert into core.tax_assessment_declarant (assessment_id, person_id, year, rank) values ('{{Notice}}', '{{Adult}}', 2024, 1);
            """, ct);

        await core.Database.MigrateAsync(ct);
        await using var tax = PostgresFixture.NewTaxContext(connectionString);
        await tax.Database.MigrateAsync(ct);

        Assert.Equal("""{"childrenUnder6": 1}""", (await tax.TaxProfiles.SingleAsync(ct)).Data);
        var notice = await tax.TaxAssessments.SingleAsync(ct);
        Assert.Equal((Notice, 2024), (notice.Id, notice.Year));
        var declarant = await tax.TaxAssessmentDeclarants.SingleAsync(ct);
        Assert.Equal((Notice, Adult, 2024, 1), (declarant.AssessmentId, declarant.PersonId, declarant.Year, declarant.Rank));
        Assert.Equal(Tables, await TablesAsync(connectionString, ct));
        Assert.Equal(["fk_tax_assessment_declarant_person_person_id"], await PersonKeysAsync(connectionString, ct));
        Assert.False(tax.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_fresh_database_keeps_the_tables_in_core_and_the_tax_schema_holds_the_history_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();

        Assert.Equal(Tables, await TablesAsync(connectionString, ct));
        Assert.Equal(["__ef_migrations_history"], await QueryAsync(connectionString,
            "select table_name::text from information_schema.tables where table_schema = 'tax'", ct));
    }

    /// <summary>
    /// The previous image's Core model (its last migration's target model) describes the same three tables, column for column, as the
    /// tax module's, and every one of its migrations stays applied: it starts on the migrated database and reads and writes them as before.
    /// </summary>
    [Fact]
    public async Task The_previous_image_s_model_still_matches_the_tables_after_this_release_s_migrations()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connectionString);
        await using var tax = PostgresFixture.NewTaxContext(connectionString);
        var migrations = core.GetService<IMigrationsAssembly>();
        var previous = migrations.CreateMigration(migrations.Migrations[BeforeTaxModule], core.Database.ProviderName!).TargetModel!;
        var current = tax.GetService<IDesignTimeModel>().Model;

        foreach (var (old, now) in new[]
                 {
                     ("Nid.Core.Infrastructure.Persistence.TaxProfileRow", typeof(TaxProfileRow)),
                     ("Nid.Core.Infrastructure.Persistence.TaxAssessmentRow", typeof(TaxAssessmentRow)),
                     ("Nid.Core.Infrastructure.Persistence.TaxAssessmentDeclarantRow", typeof(TaxAssessmentDeclarantRow)),
                 })
            Assert.Equal(Shape(previous.FindEntityType(old)!), Shape(current.FindEntityType(now)!));

        var applied = await core.Database.GetAppliedMigrationsAsync(ct);
        Assert.Contains(BeforeTaxModule, applied);
        Assert.Contains("20261027152441_TaxAssessments", applied);
        // A write as the previous image's store made it: the row version (xmin) guards the update.
        await ExecuteAsync(connectionString, $$"""
            insert into core.person (id, kind, display_name, session_epoch, preferences, created_at) values ('{{Adult}}', 'adult', 'Alice', 0, '{}', now());
            insert into core.tax_assessment (id, year, data) values ('{{Notice}}', 2024, '{}');
            insert into core.tax_assessment_declarant (assessment_id, person_id, year, rank) values ('{{Notice}}', '{{Adult}}', 2024, 1);
            update core.tax_assessment set data = '{"year":2024}' where id = '{{Notice}}' and xmin = (select xmin from core.tax_assessment where id = '{{Notice}}');
            """, ct);
        Assert.Equal("""{"year": 2024}""", (await tax.TaxAssessments.SingleAsync(ct)).Data);
    }

    [Fact]
    public async Task Without_Core_s_migrations_first_the_tax_module_s_fails_rather_than_run_without_its_tables()
    {
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var tax = PostgresFixture.NewTaxContext(connectionString);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => tax.Database.MigrateAsync(TestContext.Current.CancellationToken));

        Assert.Contains("run Core's migrations first", refused.MessageText);
    }

    /// <summary>A table as the database sees it: name, columns and types, primary key, indexes and checks.</summary>
    private static string Shape(IReadOnlyEntityType entity)
    {
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var columns = entity.GetProperties().Select(p => $"{p.GetColumnName(table)} {p.GetColumnType()}").Order(StringComparer.Ordinal);
        var indexes = entity.GetIndexes().Select(i => $"{i.GetDatabaseName(table)}{(i.IsUnique ? " unique" : "")}").Order(StringComparer.Ordinal);
        var checks = entity.GetCheckConstraints().Select(c => $"{c.Name}: {c.Sql}").Order(StringComparer.Ordinal);
        return $"{table.Schema}.{table.Name} [{string.Join(", ", columns)}] pk {entity.FindPrimaryKey()!.GetName(table)} "
            + $"[{string.Join(", ", indexes)}] [{string.Join(", ", checks)}]";
    }

    private static Task<List<string>> TablesAsync(string connectionString, CancellationToken ct)
        => QueryAsync(connectionString, """
            select table_schema || '.' || table_name from information_schema.tables
            where table_name like 'tax\_%' order by 1
            """, ct);

    /// <summary>The foreign keys from the declarants to <c>core.person</c>.</summary>
    private static Task<List<string>> PersonKeysAsync(string connectionString, CancellationToken ct)
        => QueryAsync(connectionString, """
            select conname::text from pg_constraint
            where contype = 'f' and confrelid = 'core.person'::regclass and conname like 'fk_tax_assessment_declarant%'
            """, ct);

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
            rows.Add(reader.GetString(0));
        return rows;
    }
}
