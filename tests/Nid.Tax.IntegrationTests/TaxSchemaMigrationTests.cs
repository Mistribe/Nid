using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Tax.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Tax.IntegrationTests;

/// <summary>
/// #512 in steps: the tax module took over the tables Core created, where they were (<c>core</c>, <c>InitialTax</c>); #568 moves them to
/// <c>tax</c> and leaves views under the old names, so the previous image still runs on a migrated database (architecture.md §10.1).
/// </summary>
public class TaxSchemaMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>The tax module's migration before the move: the previous image's layout.</summary>
    private const string BeforeMove = "20261123183705_InitialTax";

    private static readonly Guid Adult = Guid.Parse("01920000-0000-7000-8000-000000000001");
    private static readonly Guid Notice = Guid.Parse("01920000-0000-7000-8000-0000000000a1");

    private static readonly string[] Moved =
    [
        "core.tax_assessment VIEW", "core.tax_assessment_declarant VIEW", "core.tax_profile VIEW",
        "tax.tax_assessment BASE TABLE", "tax.tax_assessment_declarant BASE TABLE", "tax.tax_profile BASE TABLE",
    ];

    private static readonly string[] InCore =
        ["core.tax_assessment BASE TABLE", "core.tax_assessment_declarant BASE TABLE", "core.tax_profile BASE TABLE"];

    [Fact]
    public async Task An_existing_household_s_profile_and_notices_move_to_the_tax_schema_with_their_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await MigratedToAsync(BeforeMove, ct);
        await SeedAsync(connectionString, ct);

        await using var tax = PostgresFixture.NewTaxContext(connectionString);
        await tax.Database.MigrateAsync(ct);

        Assert.Equal("""{"childrenUnder6": 1}""", (await tax.TaxProfiles.SingleAsync(ct)).Data);
        var notice = await tax.TaxAssessments.SingleAsync(ct);
        Assert.Equal((Notice, 2024), (notice.Id, notice.Year));
        var declarant = await tax.TaxAssessmentDeclarants.SingleAsync(ct);
        Assert.Equal((Notice, Adult, 2024, 1), (declarant.AssessmentId, declarant.PersonId, declarant.Year, declarant.Rank));
        Assert.Equal(Moved, await RelationsAsync(connectionString, ct));
        Assert.Empty(await PersonKeysAsync(connectionString, ct));
        Assert.False(tax.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_fresh_database_ends_with_the_tables_in_tax_and_views_in_core()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();

        Assert.Equal(Moved, await RelationsAsync(connectionString, ct));
        Assert.Empty(await PersonKeysAsync(connectionString, ct));
    }

    /// <summary>
    /// The previous image maps the tables in <c>core</c>: its model's columns are the views' columns, and it reads, writes and deletes
    /// through them as it did on the tables, the row version (xmin) guarding the update.
    /// </summary>
    [Fact]
    public async Task The_previous_image_still_reads_and_writes_through_the_core_views()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var tax = PostgresFixture.NewTaxContext(connectionString);
        var migrations = tax.GetService<IMigrationsAssembly>();
        var previous = migrations.CreateMigration(migrations.Migrations[BeforeMove], tax.Database.ProviderName!).TargetModel!;

        foreach (var entity in new[] { typeof(TaxProfileRow), typeof(TaxAssessmentRow), typeof(TaxAssessmentDeclarantRow) })
        {
            var old = previous.FindEntityType(entity.FullName!)!;
            Assert.Equal("core", old.GetSchema());
            var table = StoreObjectIdentifier.Table(old.GetTableName()!, "core");
            Assert.Equal(old.GetProperties().Select(p => p.GetColumnName(table)!).Order(StringComparer.Ordinal),
                await QueryAsync(connectionString, $"""
                    select column_name::text from information_schema.columns
                    where table_schema = 'core' and table_name = '{old.GetTableName()}' order by column_name collate "C"
                    """, ct));
        }

        await SeedAsync(connectionString, ct);
        await ExecuteAsync(connectionString, $$"""
            update core.tax_assessment set data = '{"year":2024}' where id = '{{Notice}}' and xmin = (select xmin from core.tax_assessment where id = '{{Notice}}');
            update core.tax_profile set data = '{"childrenUnder6":2}' where year = 2025;
            """, ct);
        Assert.Equal("""{"year": 2024}""", (await tax.TaxAssessments.SingleAsync(ct)).Data);
        Assert.Equal("""{"childrenUnder6": 2}""", (await tax.TaxProfiles.SingleAsync(ct)).Data);

        await ExecuteAsync(connectionString, $"delete from core.tax_assessment where id = '{Notice}'", ct);
        Assert.Empty(await tax.TaxAssessmentDeclarants.ToListAsync(ct));
    }

    [Fact]
    public async Task Down_puts_the_tables_and_the_person_key_back_in_core_with_their_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await SeedAsync(connectionString, ct);
        await using var tax = PostgresFixture.NewTaxContext(connectionString);

        await tax.GetService<IMigrator>().MigrateAsync(BeforeMove, ct);

        Assert.Equal(InCore, await RelationsAsync(connectionString, ct));
        Assert.Equal(["fk_tax_assessment_declarant_person_person_id"], await PersonKeysAsync(connectionString, ct));
        Assert.Equal(["1"], await QueryAsync(connectionString, "select count(*)::text from core.tax_assessment_declarant", ct));
    }

    [Fact]
    public async Task Without_Core_s_migrations_first_the_tax_module_s_fails_rather_than_run_without_its_tables()
    {
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var tax = PostgresFixture.NewTaxContext(connectionString);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => tax.Database.MigrateAsync(TestContext.Current.CancellationToken));

        Assert.Contains("run Core's migrations first", refused.MessageText);
    }

    /// <summary>An empty database with every Core migration and the tax module's up to <paramref name="migration"/>.</summary>
    private async Task<string> MigratedToAsync(string migration, CancellationToken ct)
    {
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using (var core = PostgresFixture.NewCoreContext(connectionString))
            await core.Database.MigrateAsync(ct);
        await using (var tax = PostgresFixture.NewTaxContext(connectionString))
            await tax.GetService<IMigrator>().MigrateAsync(migration, ct);
        return connectionString;
    }

    /// <summary>A profile, a notice and its declarant, written under the old names (tables before the move, views after).</summary>
    private static Task SeedAsync(string connectionString, CancellationToken ct) => ExecuteAsync(connectionString, $$"""
        insert into core.person (id, kind, display_name, session_epoch, preferences, created_at) values
          ('{{Adult}}', 'adult', 'Alice', 0, '{}', now());
        insert into core.tax_profile (year, data) values (2025, '{"childrenUnder6":1}');
        insert into core.tax_assessment (id, year, data) values ('{{Notice}}', 2024, '{"year":2024}');
        insert into core.tax_assessment_declarant (assessment_id, person_id, year, rank) values ('{{Notice}}', '{{Adult}}', 2024, 1);
        """, ct);

    /// <summary>Every <c>tax_*</c> table and view, with its schema and kind.</summary>
    private static Task<List<string>> RelationsAsync(string connectionString, CancellationToken ct)
        => QueryAsync(connectionString, """
            select r from (select table_schema || '.' || table_name || ' ' || table_type as r from information_schema.tables
            where table_name like 'tax\_%') t order by r collate "C"
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
