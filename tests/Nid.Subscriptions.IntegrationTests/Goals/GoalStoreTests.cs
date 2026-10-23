using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Goals.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Goals;

public class GoalStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<ItemId> NewItemAsync(IServiceProvider sp, NidApp app, string kind = "savings")
    {
        sp.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await sp.GetRequiredService<IItemRegistry>().CreateAsync(
            new NewItem("goals", kind, "Goal " + Guid.NewGuid(), Visibility.Shared, null, [new OwnerShare(app.AdultA, 10_000)]), default)).Ok();
    }

    private static Goal Savings(ItemId id, decimal target = 5000m)
        => new(id, GoalKinds.Savings, target, null, new LocalDate(2030, 1, 1), "note", 0);

    [Fact]
    public async Task Round_trips_a_goal_with_funding_and_entries()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(sp, app);
        var envelope = new ItemId(Guid.CreateVersion7());

        await store.CreateAsync(Savings(id), default);
        await store.ReplaceFundingAsync(id, [new Funding(id, envelope, 2500)], default);
        await store.PutEntryAsync(new GoalEntry(id, new LocalDate(2026, 1, 5), 120.5m), default);

        var goal = (await store.FindAsync(id, default))!;
        Assert.Equal(5000m, goal.TargetAmount);
        Assert.Equal(new LocalDate(2030, 1, 1), goal.TargetDate);
        Assert.Equal("note", goal.Note);
        Assert.Single(await store.FindManyAsync([id], default));
        Assert.Equal(new Funding(id, envelope, 2500), Assert.Single(await store.FundingOfAsync([id], default)));
        Assert.Equal(new GoalEntry(id, new LocalDate(2026, 1, 5), 120.5m), Assert.Single(await store.EntriesOfAsync([id], default)));
        Assert.True(await store.DeleteEntryAsync(id, new LocalDate(2026, 1, 5), default));
        Assert.False(await store.DeleteEntryAsync(id, new LocalDate(2026, 1, 5), default));
    }

    [Fact]
    public async Task Put_entry_replaces_same_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.CreateAsync(Savings(id), default);

        await store.PutEntryAsync(new GoalEntry(id, new LocalDate(2026, 1, 5), 10m), default);
        await store.PutEntryAsync(new GoalEntry(id, new LocalDate(2026, 1, 5), 20m), default);

        Assert.Equal(20m, Assert.Single(await store.EntriesOfAsync([id], default)).Amount);
    }

    [Fact]
    public async Task Funding_on_envelope_lists_every_goal_linking_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var one = await NewItemAsync(scope.ServiceProvider, app);
        var two = await NewItemAsync(scope.ServiceProvider, app);
        var envelope = new ItemId(Guid.CreateVersion7());
        await store.CreateAsync(Savings(one), default);
        await store.CreateAsync(Savings(two), default);
        await store.ReplaceFundingAsync(one, [new Funding(one, envelope, 4000)], default);
        await store.ReplaceFundingAsync(two, [new Funding(two, envelope, 6000)], default);

        var on = await store.FundingOnAsync([envelope], default);

        Assert.Equal(new[] { one.Value, two.Value }.Order(), on.Select(f => f.Goal.Value).Order());
    }

    [Fact]
    public async Task Update_with_stale_version_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.CreateAsync(Savings(id), default);
        var read = (await store.FindAsync(id, default))!;

        (await store.UpdateAsync(read with { TargetAmount = 6000m }, default)).Ok();
        var stale = await store.UpdateAsync(read with { TargetAmount = 7000m }, default);

        Assert.Equal(GoalsCodes.Conflict, stale.FailureCode());
        Assert.Equal(6000m, (await store.FindAsync(id, default))!.TargetAmount);
    }

    [Fact]
    public async Task Replace_funding_twice_with_same_envelope_changes_the_share()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        var envelope = new ItemId(Guid.CreateVersion7());
        await store.CreateAsync(Savings(id), default);

        await store.ReplaceFundingAsync(id, [new Funding(id, envelope, 6000)], default);
        await store.ReplaceFundingAsync(id, [new Funding(id, envelope, 4000)], default);

        Assert.Equal(new Funding(id, envelope, 4000), Assert.Single(await store.FundingOfAsync([id], default)));
    }
}
