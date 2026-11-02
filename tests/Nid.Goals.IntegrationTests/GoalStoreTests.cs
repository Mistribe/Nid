using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Goals.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Goals.IntegrationTests;

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

        await store.CreateAsync(Savings(id), TestContext.Current.CancellationToken);
        await store.ReplaceFundingAsync(id, [new Funding(id, envelope, 2500)], TestContext.Current.CancellationToken);
        await store.PutEntryAsync(new GoalEntry(id, new LocalDate(2026, 1, 5), 120.5m), TestContext.Current.CancellationToken);

        var goal = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(5000m, goal.TargetAmount);
        Assert.Equal(new LocalDate(2030, 1, 1), goal.TargetDate);
        Assert.Equal("note", goal.Note);
        Assert.Single(await store.FindManyAsync([id], TestContext.Current.CancellationToken));
        Assert.Equal(new Funding(id, envelope, 2500), Assert.Single(await store.FundingOfAsync([id], TestContext.Current.CancellationToken)));
        Assert.Equal(new GoalEntry(id, new LocalDate(2026, 1, 5), 120.5m), Assert.Single(await store.EntriesOfAsync([id], TestContext.Current.CancellationToken)));
        Assert.True(await store.DeleteEntryAsync(id, new LocalDate(2026, 1, 5), TestContext.Current.CancellationToken));
        Assert.False(await store.DeleteEntryAsync(id, new LocalDate(2026, 1, 5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Put_entry_replaces_same_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.CreateAsync(Savings(id), TestContext.Current.CancellationToken);

        await store.PutEntryAsync(new GoalEntry(id, new LocalDate(2026, 1, 5), 10m), TestContext.Current.CancellationToken);
        await store.PutEntryAsync(new GoalEntry(id, new LocalDate(2026, 1, 5), 20m), TestContext.Current.CancellationToken);

        Assert.Equal(20m, Assert.Single(await store.EntriesOfAsync([id], TestContext.Current.CancellationToken)).Amount);
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
        await store.CreateAsync(Savings(one), TestContext.Current.CancellationToken);
        await store.CreateAsync(Savings(two), TestContext.Current.CancellationToken);
        await store.ReplaceFundingAsync(one, [new Funding(one, envelope, 4000)], TestContext.Current.CancellationToken);
        await store.ReplaceFundingAsync(two, [new Funding(two, envelope, 6000)], TestContext.Current.CancellationToken);

        var on = await store.FundingOnAsync([envelope], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { one.Value, two.Value }.Order(), on.Select(f => f.Goal.Value).Order());
    }

    [Fact]
    public async Task Update_with_stale_version_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.CreateAsync(Savings(id), TestContext.Current.CancellationToken);
        var read = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;

        (await store.UpdateAsync(read with { TargetAmount = 6000m }, TestContext.Current.CancellationToken)).Ok();
        var stale = await store.UpdateAsync(read with { TargetAmount = 7000m }, TestContext.Current.CancellationToken);

        Assert.Equal(GoalsCodes.Conflict, stale.FailureCode());
        Assert.Equal(6000m, (await store.FindAsync(id, TestContext.Current.CancellationToken))!.TargetAmount);
    }

    [Fact]
    public async Task Replace_funding_twice_with_same_envelope_changes_the_share()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        var envelope = new ItemId(Guid.CreateVersion7());
        await store.CreateAsync(Savings(id), TestContext.Current.CancellationToken);

        await store.ReplaceFundingAsync(id, [new Funding(id, envelope, 6000)], TestContext.Current.CancellationToken);
        await store.ReplaceFundingAsync(id, [new Funding(id, envelope, 4000)], TestContext.Current.CancellationToken);

        Assert.Equal(new Funding(id, envelope, 4000), Assert.Single(await store.FundingOfAsync([id], TestContext.Current.CancellationToken)));
    }
}
