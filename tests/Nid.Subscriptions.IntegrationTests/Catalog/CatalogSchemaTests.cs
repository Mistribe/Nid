using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using Npgsql;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

[Collection("postgres")]
public class CatalogSchemaTests(PostgresFixture fixture)
{
    /// <summary>
    /// Design §3.4: keys written before any catalog (1C1's API, 1D3's import) become adopted providers in the
    /// migration that adds the foreign key, so it holds from the first start. A blank key names nothing.
    /// </summary>
    [Fact]
    public async Task Keys_stored_before_the_catalog_are_adopted_and_the_foreign_key_holds()
    {
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using (var subs = PostgresFixture.NewSubscriptionsContext(connectionString))
        {
            await subs.GetService<IMigrator>().MigrateAsync("ImportRefs");
            await subs.Database.ExecuteSqlRawAsync("""
                insert into subscriptions.subscription (item_id, provider_key, auto_renew) values
                  (gen_random_uuid(), 'wow', true), (gen_random_uuid(), 'wow', true),
                  (gen_random_uuid(), 'amazon-prime', true), (gen_random_uuid(), 'Spotify Premium', true),
                  (gen_random_uuid(), '  ', true), (gen_random_uuid(), null, true)
                """);
            await subs.Database.MigrateAsync();
        }

        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        var adopted = (await db.Providers.AsNoTracking().ToListAsync()).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(["Spotify Premium", "amazon-prime", "wow"], adopted.Select(p => p.Key));
        Assert.Equal(["Spotify Premium", "Amazon prime", "Wow"], adopted.Select(p => p.Name));
        Assert.All(adopted, p => Assert.Equal(("adopted", CatalogLimits.OtherLabel), (p.Origin, Assert.Single(p.LabelKeys))));

        var other = await db.Labels.AsNoTracking().SingleAsync();
        Assert.Equal((CatalogLimits.OtherLabel, "Other", CatalogLimits.NeutralColor, "local"), (other.Key, other.Name, other.Color, other.Origin));
        Assert.Equal(2, await db.Subscriptions.CountAsync(s => s.ProviderKey == null)); // the blank key and the null one

        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "insert into subscriptions.subscription (item_id, provider_key, auto_renew) values (gen_random_uuid(), 'nope', true)"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    [Fact]
    public async Task An_empty_database_migrates_with_the_local_label_only()
    {
        await using var db = PostgresFixture.NewSubscriptionsContext(await fixture.NewConnectionStringAsync());

        Assert.False(await db.Providers.AnyAsync());
        Assert.False(await db.CatalogSyncRuns.AnyAsync());
        Assert.Equal([CatalogLimits.OtherLabel], await db.Labels.Select(l => l.Key).ToListAsync());
    }

    [Theory]
    [InlineData("insert into subscriptions.provider (key, origin, name, label_keys) values ('Netflix', 'catalog', 'N', array['other'])")]
    [InlineData("insert into subscriptions.provider (key, origin, name, label_keys, catalog_commit) values ('netflix', 'catalog', 'N', array[]::text[], 'c')")]
    [InlineData("insert into subscriptions.provider (key, origin, name, label_keys) values ('tinder-k3f9qa', 'custom', 'T', array['other'])")]
    [InlineData("insert into subscriptions.label (key, name, color, origin) values ('vod', 'VOD', '#FFF06292', 'catalog')")]
    public async Task The_database_refuses_what_the_rules_refuse(string sql)
    {
        await using var db = PostgresFixture.NewSubscriptionsContext(await fixture.NewConnectionStringAsync());
        var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }
}
