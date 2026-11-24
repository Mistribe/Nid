using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Banking.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Banking.IntegrationTests;

/// <summary>
/// #513: Banking's settings move from <c>core.household_settings</c> to <c>banking.settings</c>; Banking's migration, run after Core's,
/// copies them and drops the old columns, and its <c>Down</c> puts them back.
/// </summary>
public class BankingSettingsMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Banking's migration before the move: the previous image's layout.</summary>
    private const string BeforeMove = "20261121164505_GenericBankPrefixes457";

    private const string OldColumns = """
        select column_name::text from information_schema.columns
        where table_schema = 'core' and table_name = 'household_settings' and column_name in ('enable_banking_app_id', 'bank_retention_months')
        order by column_name
        """;

    [Fact]
    public async Task An_existing_household_s_banking_settings_move_to_the_banking_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using (var core = PostgresFixture.NewCoreContext(connectionString))
            await core.Database.MigrateAsync(ct);
        await using var banking = PostgresFixture.NewBankingContext(connectionString);
        await banking.GetService<IMigrator>().MigrateAsync(BeforeMove, ct);
        await ExecuteAsync(connectionString, """
            insert into core.household_settings (id, name, reference_currency, timezone, locale, max_adults, enable_banking_app_id, bank_retention_months)
            values (1, 'Nid', 'EUR', 'Europe/Paris', 'fr-FR', 2, 'app-1', 24);
            """, ct);

        await banking.Database.MigrateAsync(ct);

        var settings = await banking.Settings.SingleAsync(ct);
        Assert.Equal(("app-1", 24), (settings.EnableBankingAppId, settings.BankRetentionMonths));
        Assert.Empty(await QueryAsync(connectionString, OldColumns, ct));
        Assert.False(banking.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_fresh_database_has_an_empty_settings_row_and_no_old_columns()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var banking = PostgresFixture.NewBankingContext(connectionString);

        var settings = await banking.Settings.SingleAsync(ct);
        Assert.Equal((null, null), (settings.EnableBankingAppId, settings.BankRetentionMonths));
        Assert.Empty(await QueryAsync(connectionString, OldColumns, ct));
    }

    [Fact]
    public async Task Down_puts_the_columns_back_in_core_with_their_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.NewConnectionStringAsync();
        await ExecuteAsync(connectionString, """
            insert into core.household_settings (id, name, reference_currency, timezone, locale, max_adults) values (1, 'Nid', 'EUR', 'Europe/Paris', 'fr-FR', 2);
            update banking.settings set enable_banking_app_id = 'app-1', bank_retention_months = 24;
            """, ct);
        await using var banking = PostgresFixture.NewBankingContext(connectionString);

        await banking.GetService<IMigrator>().MigrateAsync(BeforeMove, ct);

        Assert.Equal(["app-1|24"], await QueryAsync(connectionString,
            "select enable_banking_app_id || '|' || bank_retention_months from core.household_settings", ct));
        Assert.Equal(["ck_household_settings_bank_retention"], await QueryAsync(connectionString,
            "select conname::text from pg_constraint where conname = 'ck_household_settings_bank_retention'", ct));
        Assert.Equal(["0"], await QueryAsync(connectionString,
            "select count(*)::text from information_schema.tables where table_schema = 'banking' and table_name = 'settings'", ct));
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
