using Microsoft.EntityFrameworkCore;
using Nid.Core.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

[Collection("postgres")]
public class SchemaTests(PostgresFixture fixture)
{
    /// <summary>
    /// The property under test is that both modules genuinely coexist on the same database: a write through
    /// one module's context and a write through the other's, each read back correctly through its own schema.
    /// </summary>
    [Fact]
    public async Task Both_modules_write_and_read_their_own_rows_on_the_same_database()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        var personId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();

        await using (var core = PostgresFixture.NewCoreContext(connectionString))
        {
            core.Persons.Add(new PersonRow
            {
                Id = personId,
                Kind = "dependent",
                DisplayName = "Kid",
                CreatedAt = PostgresFixture.Clock.GetCurrentInstant(),
            });
            await core.SaveChangesAsync();
        }

        await using (var subs = PostgresFixture.NewSubscriptionsContext(connectionString))
        {
            subs.Providers.Add(new ProviderRow { Key = "spotify", Origin = "catalog", Name = "Spotify", LabelKeys = ["other"], CatalogCommit = "test" });
            subs.Subscriptions.Add(new SubscriptionRow
            {
                ItemId = itemId,
                ProviderKey = "spotify",
                AutoRenew = true,
            });
            await subs.SaveChangesAsync();
        }

        await using (var core = PostgresFixture.NewCoreContext(connectionString))
        {
            var person = await core.Persons.SingleAsync(p => p.Id == personId);
            Assert.Equal("Kid", person.DisplayName);
            Assert.Equal("dependent", person.Kind);
        }

        await using (var subs = PostgresFixture.NewSubscriptionsContext(connectionString))
        {
            var subscription = await subs.Subscriptions.SingleAsync(s => s.ItemId == itemId);
            Assert.Equal("spotify", subscription.ProviderKey);
            Assert.True(subscription.AutoRenew);
        }
    }

    [Fact]
    public async Task A_billing_phase_with_a_negative_price_is_rejected_by_the_database()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        var itemId = Guid.CreateVersion7();

        await using var subs = PostgresFixture.NewSubscriptionsContext(connectionString);
        subs.Subscriptions.Add(new SubscriptionRow { ItemId = itemId });
        await subs.SaveChangesAsync();

        subs.Phases.Add(new BillingPhaseRow
        {
            Id = Guid.CreateVersion7(),
            ItemId = itemId,
            StartsOn = new LocalDate(2026, 1, 1),
            PriceAmount = -1m,
            EveryCount = 1,
            PayerType = "household",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => subs.SaveChangesAsync());
    }

    [Fact]
    public async Task A_billing_phase_with_an_invalid_payer_type_is_rejected_by_the_database()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        var itemId = Guid.CreateVersion7();

        await using var subs = PostgresFixture.NewSubscriptionsContext(connectionString);
        subs.Subscriptions.Add(new SubscriptionRow { ItemId = itemId });
        await subs.SaveChangesAsync();

        subs.Phases.Add(new BillingPhaseRow
        {
            Id = Guid.CreateVersion7(),
            ItemId = itemId,
            StartsOn = new LocalDate(2026, 1, 1),
            PriceAmount = 9.99m,
            EveryCount = 1,
            PayerType = "external", // valid on purchase, not on billing_phase
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => subs.SaveChangesAsync());
    }

    [Fact]
    public async Task Each_module_keeps_its_own_migrations_history_in_its_own_schema()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connectionString);

        var schemas = await core.Database
            .SqlQueryRaw<string>("select table_schema from information_schema.tables where table_name = '__ef_migrations_history'")
            .ToListAsync();

        Assert.Contains("core", schemas);
        Assert.Contains("subscriptions", schemas);
        Assert.Contains("investments", schemas);
        Assert.Contains("income", schemas);
        Assert.Contains("platform", schemas);
        Assert.Equal(5, schemas.Count);
    }

    [Fact]
    public async Task The_subscriptions_tables_live_in_the_subscriptions_schema()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connectionString);

        var tables = await core.Database
            .SqlQueryRaw<string>("select table_name from information_schema.tables where table_schema = 'subscriptions' order by table_name")
            .ToListAsync();

        Assert.Equal(
            ["__ef_migrations_history", "billing_phase", "catalog_sync_run", "coverage_grant", "label", "provider", "purchase", "subscription", "subscription_user"],
            tables);
    }

    [Fact]
    public async Task The_platform_schema_is_migrated_beside_the_two_modules()
    {
        var connectionString = await fixture.NewConnectionStringAsync();

        await using var platform = PostgresFixture.NewPlatformContext(connectionString);
        Assert.Empty(await platform.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await platform.Sessions.CountAsync());
    }

    [Fact]
    public async Task The_Quartz_tables_live_in_the_platform_schema_and_the_rates_in_core()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connectionString);

        var quartz = await core.Database
            .SqlQueryRaw<string>("select table_name as \"Value\" from information_schema.tables where table_schema = 'platform' and table_name like 'qrtz%' order by table_name")
            .ToListAsync();
        Assert.Equal(
            ["qrtz_blob_triggers", "qrtz_calendars", "qrtz_cron_triggers", "qrtz_fired_triggers", "qrtz_job_details", "qrtz_locks",
             "qrtz_paused_job_grps", "qrtz_paused_trigger_grps", "qrtz_scheduler_state", "qrtz_simple_triggers", "qrtz_simprop_triggers", "qrtz_triggers"],
            quartz);

        var fx = await core.Database
            .SqlQueryRaw<string>("select table_name as \"Value\" from information_schema.tables where table_schema = 'core' and table_name like 'fx%' order by table_name")
            .ToListAsync();
        Assert.Equal(["fx_rate", "fx_sync_run"], fx);

        // The rate reads go by quote (FxRateStore.ReadAsync, PreviousAsync): an index leads with it.
        var quoteIndex = await core.Database
            .SqlQueryRaw<string>("select indexdef as \"Value\" from pg_indexes where schemaname = 'core' and indexname = 'ix_fx_rate_quote_on_date'")
            .SingleAsync();
        Assert.EndsWith("(quote, on_date)", quoteIndex);
    }

    /// <summary>Import provenance design §3: an import ref is unique per importer, and a row with an importer is an imported row.</summary>
    [Theory]
    [InlineData("subscription")]
    [InlineData("billing_phase")]
    [InlineData("purchase")]
    public async Task An_import_ref_is_unique_per_importer(string table)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(await fixture.NewConnectionStringAsync());

        var index = await subs.Database
            .SqlQuery<string>($"select indexdef as \"Value\" from pg_indexes where schemaname = 'subscriptions' and indexname = {$"ix_{table}_imported_by_import_ref"}")
            .SingleAsync();
        var old = await subs.Database
            .SqlQuery<string>($"select indexname::text as \"Value\" from pg_indexes where schemaname = 'subscriptions' and indexname = {$"ix_{table}_import_ref"}")
            .ToListAsync();
        var check = await subs.Database
            .SqlQuery<string>($"select conname::text as \"Value\" from pg_constraint where conname = {$"ck_{table}_imported_by"}")
            .ToListAsync();

        Assert.StartsWith("CREATE UNIQUE INDEX", index);
        Assert.EndsWith("(imported_by, import_ref) WHERE (import_ref IS NOT NULL)", index);
        Assert.Empty(old);
        Assert.Single(check);
    }
}
