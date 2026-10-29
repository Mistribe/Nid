using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

public class ItemReplaceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Replace_updates_name_visibility_and_owners_in_place_and_the_audit_keeps_the_old_values()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Ancien nom", app.AdultA, Visibility.Private);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA) with { Via = ActorOrigin.Import };
            var registry = scope.ServiceProvider.GetRequiredService<IItemRegistry>();
            var replaced = await registry.ReplaceAsync(id, "Nouveau nom", Visibility.Shared,
                [new OwnerShare(app.AdultA, 5000), new OwnerShare(app.AdultB, 5000)], CancellationToken.None);
            Assert.True(replaced.IsSuccess);
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var item = await core.Items.Include(i => i.Owners).SingleAsync(i => i.Id == id.Value);
        Assert.Equal(("Nouveau nom", "shared"), (item.DisplayName, item.Visibility));
        Assert.Equal(2, item.Owners.Count);
        Assert.Equal(5000, item.Owners.Single(o => o.PersonId == app.AdultA.Value).ShareBp);
        Assert.Equal(5000, item.Owners.Single(o => o.PersonId == app.AdultB.Value).ShareBp);

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var update = await platform.AuditLog.SingleAsync(r => r.Category == "write" && r.Action == "update" && r.Entity == "core.item");
        Assert.Contains("Ancien nom", update.Data);
        Assert.Contains("Nouveau nom", update.Data);
        Assert.Equal("import", update.Via);
    }

    [Fact]
    public async Task Replace_from_two_owners_to_one_removes_the_dropped_ownership_row_and_the_audit_shows_the_delete()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Deux propriétaires", Visibility.Shared,
            (app.AdultA, 5000), (app.AdultB, 5000));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA) with { Via = ActorOrigin.Import };
            var registry = scope.ServiceProvider.GetRequiredService<IItemRegistry>();
            var replaced = await registry.ReplaceAsync(id, "Un seul propriétaire", Visibility.Private,
                [new OwnerShare(app.AdultA, 10000)], CancellationToken.None);
            Assert.True(replaced.IsSuccess);
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var item = await core.Items.Include(i => i.Owners).SingleAsync(i => i.Id == id.Value);
        Assert.Equal([(app.AdultA.Value, 10000)], item.Owners.Select(o => (o.PersonId, o.ShareBp)).ToArray());

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var removed = await platform.AuditLog.SingleAsync(
            r => r.Category == "write" && r.Action == "delete" && r.Entity == "core.ownership" && r.ItemIds.Contains(id.Value));
        Assert.Equal("import", removed.Via);
    }

    [Fact]
    public async Task Replace_refuses_owners_that_break_the_ownership_rules_and_changes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Nom", app.AdultA, Visibility.Private);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var registry = scope.ServiceProvider.GetRequiredService<IItemRegistry>();

        var refused = await registry.ReplaceAsync(id, "Nom", Visibility.Shared, [new OwnerShare(app.AdultA, 4000)], CancellationToken.None);

        Assert.False(refused.IsSuccess); // shares must sum to 10 000
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(10000, (await core.Items.Include(i => i.Owners).SingleAsync(i => i.Id == id.Value)).Owners.Sum(o => o.ShareBp));
    }
}
