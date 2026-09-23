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
        Assert.Contains("platform", schemas);
        Assert.Equal(3, schemas.Count);
    }

    [Fact]
    public async Task The_subscriptions_tables_live_in_the_subscriptions_schema()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var core = PostgresFixture.NewCoreContext(connectionString);

        var tables = await core.Database
            .SqlQueryRaw<string>("select table_name from information_schema.tables where table_schema = 'subscriptions' order by table_name")
            .ToListAsync();

        Assert.Equal(["__ef_migrations_history", "billing_phase", "coverage_grant", "purchase", "subscription", "subscription_user"], tables);
    }

    [Fact]
    public async Task The_platform_schema_is_migrated_beside_the_two_modules()
    {
        var connectionString = await fixture.NewConnectionStringAsync();

        await using var platform = PostgresFixture.NewPlatformContext(connectionString);
        Assert.Empty(await platform.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await platform.Sessions.CountAsync());
    }
}
