using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Tax.IntegrationTests;

/// <summary>
/// #512: the tax module's first migration moves the tables Core created to the <c>tax</c> schema with their rows, on a database
/// that already holds tax data as on a fresh one, and its <c>Down</c> moves them back.
/// </summary>
public class TaxSchemaMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Core's last migration before the tax module left it: a household whose tax data still lives in <c>core</c>.</summary>
    private const string BeforeTaxModule = "20261123073823_AllowAssistantOrigin";

    private static readonly Guid Adult = Guid.Parse("01920000-0000-7000-8000-000000000001");
    private static readonly Guid Notice = Guid.Parse("01920000-0000-7000-8000-0000000000a1");

    [Fact]
    public async Task An_existing_household_keeps_its_profile_and_notices_in_the_tax_schema()
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
        Assert.Equal(["tax.tax_assessment", "tax.tax_assessment_declarant", "tax.tax_profile"], await TablesAsync(connectionString, ct));
        Assert.Empty(await PersonKeysAsync(connectionString, ct));
        Assert.False(tax.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_fresh_database_ends_with_the_tables_in_the_tax_schema_only()
    {
        var connectionString = await fixture.NewConnectionStringAsync();

        Assert.Equal(["tax.tax_assessment", "tax.tax_assessment_declarant", "tax.tax_profile"], await TablesAsync(connectionString, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Down_moves_the_tables_back_to_core_with_the_person_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var tax = PostgresFixture.NewTaxContext(connectionString);

        await tax.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase, ct);

        Assert.Equal(["core.tax_assessment", "core.tax_assessment_declarant", "core.tax_profile"], await TablesAsync(connectionString, ct));
        Assert.Equal(["fk_tax_assessment_declarant_person_person_id"], await PersonKeysAsync(connectionString, ct));
    }

    [Fact]
    public async Task Without_Core_s_migrations_first_the_move_fails_rather_than_leave_the_module_empty()
    {
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var tax = PostgresFixture.NewTaxContext(connectionString);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => tax.Database.MigrateAsync(TestContext.Current.CancellationToken));

        Assert.Contains("run Core's migrations first", refused.MessageText);
    }

    private static async Task<List<string>> TablesAsync(string connectionString, CancellationToken ct)
        => await QueryAsync(connectionString, """
            select table_schema || '.' || table_name from information_schema.tables
            where table_name like 'tax\_%' order by 1
            """, ct);

    /// <summary>The foreign keys from the declarants to <c>core.person</c>, wherever the table is.</summary>
    private static async Task<List<string>> PersonKeysAsync(string connectionString, CancellationToken ct)
        => await QueryAsync(connectionString, """
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
