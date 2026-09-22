using Microsoft.EntityFrameworkCore;
using Nid.Core.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

[Collection("postgres")]
public class SchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Both_modules_migrate_into_one_empty_database()
    {
        var connectionString = await fixture.NewConnectionStringAsync();

        await using var core = PostgresFixture.NewCoreContext(connectionString);
        await using var subs = PostgresFixture.NewSubscriptionsContext(connectionString);

        Assert.True(await core.Items.AnyAsync() || !await core.Items.AnyAsync());   // the core schema answers
        Assert.True(await subs.Subscriptions.AnyAsync() || !await subs.Subscriptions.AnyAsync());
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
        Assert.Equal(2, schemas.Count);
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
}
