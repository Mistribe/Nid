using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Banking.Contracts;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>
/// #414 step 2, an installation that never ran the transfer: its connections get the installation id, those that are not their creator's
/// own end with <see cref="BankingCodes.ApplicationTransferred"/> (which raises <c>banking.application_needed</c>), and the id goes.
/// </summary>
public class RemoveInstallationApplicationMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Before = "20261121164505_GenericBankPrefixes457";
    private const string Alice = "01920000-0000-7000-8000-00000000000a";
    private const string Bob = "01920000-0000-7000-8000-00000000000b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connections_left_on_the_installation_application_end_and_its_id_goes()
    {
        var connectionString = await postgres.NewEmptyConnectionStringAsync();
        await using (var core = PostgresFixture.NewCoreContext(connectionString))
            await core.Database.MigrateAsync(Ct);
        await using var banking = PostgresFixture.NewBankingContext(connectionString);
        var migrator = banking.GetService<IMigrator>();
        await migrator.MigrateAsync(Before, Ct);

        // Alice was given the installation application « app-home » (the transfer); Bob never declared one.
        await banking.Database.ExecuteSqlRawAsync($"""
            insert into core.household_settings (id, name, locale, reference_currency, timezone, enable_banking_app_id)
            values (1, 'Foyer', 'fr-FR', 'EUR', 'Europe/Paris', 'app-home')
            on conflict (id) do update set enable_banking_app_id = excluded.enable_banking_app_id;
            insert into banking.application (person_id, application_id, private_key, key_loaded_at, created_at)
            values ('{Alice}', 'app-home', '\x00', now(), now());
            insert into banking.connection (id, person_id, source, aspsp_name, aspsp_country, status, consecutive_failures, created_at, application_id) values
              ('01920000-0000-7000-8000-000000000001', '{Alice}', 'enable_banking', 'Banque Alpha', 'FR', 'active', 0, now(), null),
              ('01920000-0000-7000-8000-000000000002', '{Bob}', 'enable_banking', 'Banque Beta', 'FR', 'active', 0, now(), 'app-home'),
              ('01920000-0000-7000-8000-000000000003', '{Bob}', 'enable_banking', 'Banque Beta', 'FR', 'expired', 0, now(), null),
              ('01920000-0000-7000-8000-000000000004', '{Bob}', 'enable_banking', 'Banque Beta', 'FR', 'pending', 0, now(), 'app-home'),
              ('01920000-0000-7000-8000-000000000005', '{Bob}', 'file', '', '', 'active', 0, now(), null);
            """, Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        var rows = (await banking.Connections.AsNoTracking().ToListAsync(Ct)).ToDictionary(c => c.Id.ToString()[^1], c => (c.Status, c.ApplicationId, c.LastErrorCode));
        Assert.Equal(("active", "app-home", null), rows['1']);
        Assert.Equal(("revoked", "app-home", BankingCodes.ApplicationTransferred), rows['2']);
        Assert.Equal(("revoked", "app-home", BankingCodes.ApplicationTransferred), rows['3']);
        Assert.False(rows.ContainsKey('4'));
        Assert.Equal(("active", null, null), rows['5']);
        Assert.Equal(0, await banking.Database.SqlQueryRaw<int>("""
            select count(*)::int as "Value" from information_schema.columns
            where table_schema = 'core' and table_name = 'household_settings' and column_name = 'enable_banking_app_id'
            """).SingleAsync(Ct));
    }
}
