using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Investments.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Investments.IntegrationTests;

/// <summary>
/// #513: the fee references move from <c>core.household_settings</c> to <c>investments.settings</c>; Investments' migration, run after
/// Core's, copies them and drops the old column, and its <c>Down</c> puts it back.
/// </summary>
public class InvestmentsSettingsMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Investments' migration before the move: the previous image's layout.</summary>
    private const string BeforeMove = "20261123113747_EmployeeSavingsEarlyRelease";

    /// <summary>As Core wrote it: default options, so PascalCase keys.</summary>
    private const string Saved = """{"AssumedReturnPct": 5, "Envelopes": {"pea": 0.3}, "Holdings": {"etf": 0.1}}""";

    private const string OldColumn = """
        select column_name::text from information_schema.columns
        where table_schema = 'core' and table_name = 'household_settings' and column_name = 'fee_references'
        """;

    [Fact]
    public async Task An_existing_household_s_fee_references_move_to_the_investments_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using (var core = PostgresFixture.NewCoreContext(connectionString))
            await core.Database.MigrateAsync(ct);
        await using var investments = PostgresFixture.NewInvestmentsContext(connectionString);
        await investments.GetService<IMigrator>().MigrateAsync(BeforeMove, ct);
        await ExecuteAsync(connectionString, $"""
            insert into core.household_settings (id, name, reference_currency, timezone, locale, max_adults, fee_references)
            values (1, 'Nid', 'EUR', 'Europe/Paris', 'fr-FR', 2, '{Saved}');
            """, ct);

        await investments.Database.MigrateAsync(ct);

        var references = await new SettingsStore(investments).FeeReferencesAsync(ct);
        Assert.NotNull(references);
        Assert.Equal((5m, 0.3m, 0.1m), (references.AssumedReturnPct, references.Envelopes["pea"], references.Holdings["etf"]));
        Assert.Empty(await QueryAsync(connectionString, OldColumn, ct));
        Assert.False(investments.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_fresh_database_has_an_empty_settings_row_and_no_old_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var investments = PostgresFixture.NewInvestmentsContext(connectionString);

        Assert.Null((await investments.Settings.SingleAsync(ct)).FeeReferences);
        Assert.Empty(await QueryAsync(connectionString, OldColumn, ct));
    }

    [Fact]
    public async Task Down_puts_the_column_back_in_core_with_its_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await ExecuteAsync(connectionString, $"""
            insert into core.household_settings (id, name, reference_currency, timezone, locale, max_adults) values (1, 'Nid', 'EUR', 'Europe/Paris', 'fr-FR', 2);
            update investments.settings set fee_references = '{Saved}';
            """, ct);
        await using var investments = PostgresFixture.NewInvestmentsContext(connectionString);

        await investments.GetService<IMigrator>().MigrateAsync(BeforeMove, ct);

        Assert.Equal(["5"], await QueryAsync(connectionString, "select fee_references->>'AssumedReturnPct' from core.household_settings", ct));
        Assert.Equal(["0"], await QueryAsync(connectionString,
            "select count(*)::text from information_schema.tables where table_schema = 'investments' and table_name = 'settings'", ct));
    }

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
