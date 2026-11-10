using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class ItemDeletionTests(PostgresFixture postgres)
{
    private static readonly Instant Now = Instant.FromUtc(2026, 1, 15, 9, 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Refuses every delete: what a module holding a reference to the item answers.</summary>
    private sealed class BlockingPart : IItemDeletionPart
    {
        public Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionCount>>([]);

        public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeletionBlocker>>([new DeletionBlocker("goal_funding", 1, [new DeletionBlockerItem(Guid.CreateVersion7(), "Vacances")])]);

        public Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct) => Task.FromResult(false);

        public Task RemoveAsync(ItemAccess item, CancellationToken ct) => throw new InvalidOperationException("A blocked delete removes nothing.");
    }

    private async Task<ServiceProvider> ProviderAsync(bool blocked = false)
    {
        var services = new ServiceCollection();
        services.AddNid(await postgres.NewConnectionStringAsync());
        if (blocked)
            services.AddScoped<IItemDeletionPart, BlockingPart>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Alice and Bob own the item; Alice is returned.</summary>
    private static async Task<(PersonId Alice, ItemId Item)> SeedAsync(IServiceProvider services)
    {
        services.GetRequiredService<IActorAccessor>().Current = Actor.System; // every audited write needs an actor
        var persons = services.GetRequiredService<IPersonRegistry>();
        var alice = (await persons.ProvisionAdultAsync("sub-a", "Alice", null, false, Ct)).Ok();
        var bob = (await persons.ProvisionAdultAsync("sub-b", "Bob", null, false, Ct)).Ok();
        var item = (await services.GetRequiredService<IItemRegistry>().CreateAsync(new NewItem("investments", "envelope", "PEA", Visibility.Shared, "EUR",
            [new OwnerShare(alice.Id, 5_000), new OwnerShare(bob.Id, 5_000)]), Ct)).Ok();
        return (alice.Id, item);
    }

    private static FlowRow Flow(ItemId item, PersonId by, int day) => new()
    {
        Id = Guid.CreateVersion7(), ItemId = item.Value, OccurredOn = new LocalDate(2026, 1, day), Kind = "contribution", Amount = 100m,
        Currency = "EUR", Source = "manual", CreatedVia = "ui", CreatedByPersonId = by.Value, CreatedAt = Now,
    };

    private static ValuationRow Valuation(ItemId item, PersonId by, decimal amount) => new()
    {
        Id = Guid.CreateVersion7(), ItemId = item.Value, AsOf = new LocalDate(2026, 1, 10), Amount = amount, Currency = "EUR",
        Source = "manual", CreatedVia = "ui", CreatedByPersonId = by.Value, CreatedAt = Now,
    };

    /// <summary>One flow, and a valuation corrected once: the retired row and the one that supersedes it.</summary>
    private static async Task AddFactsAsync(CoreDbContext db, ItemId item, PersonId by)
    {
        var retired = Valuation(item, by, 1_000m);
        retired.RetiredAt = Now;
        var current = Valuation(item, by, 1_100m);
        current.SupersedesId = retired.Id;
        db.Flows.Add(Flow(item, by, 5));
        db.Valuations.AddRange(retired, current);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<(int Items, int Owners, int Flows, int Valuations)> RowsAsync(CoreDbContext db, ItemId item)
        => (await db.Items.CountAsync(i => i.Id == item.Value, Ct), await db.Ownerships.CountAsync(o => o.ItemId == item.Value, Ct),
            await db.Flows.CountAsync(f => f.ItemId == item.Value, Ct), await db.Valuations.CountAsync(v => v.ItemId == item.Value, Ct));

    [Fact]
    public async Task Delete_removes_item_ownership_and_facts()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        await AddFactsAsync(db, item, alice);
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
        var preview = await deletions.PreviewAsync(item, Ct);

        Assert.True((await deletions.DeleteAsync(item, preview!.Version, Ct)).IsSuccess);

        Assert.Equal((0, 0, 0, 0), await RowsAsync(db, item));
    }

    [Fact]
    public async Task Stale_version_is_refused_and_keeps_everything()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        await AddFactsAsync(db, item, alice);
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
        var preview = await deletions.PreviewAsync(item, Ct);
        var items = scope.ServiceProvider.GetRequiredService<IItemRegistry>();
        var found = (await items.FindAsync(item, Ct))!;
        Assert.True((await items.ReplaceAsync(item, "PEA Alice", found.Visibility,
            found.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), Ct)).IsSuccess);

        Assert.True((await deletions.DeleteAsync(item, preview!.Version, Ct)).TryGetFailure(out var failure));

        Assert.Equal(ItemCodes.Changed, failure!.Code);
        Assert.Equal((1, 2, 1, 2), await RowsAsync(db, item));
    }

    [Fact]
    public async Task Blocker_refuses_and_keeps_everything()
    {
        await using var provider = await ProviderAsync(blocked: true);
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        await AddFactsAsync(db, item, alice);
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
        var preview = await deletions.PreviewAsync(item, Ct);
        Assert.Equal("goal_funding", Assert.Single(preview!.Blockers).Key);

        Assert.True((await deletions.DeleteAsync(item, preview.Version, Ct)).TryGetFailure(out var failure));

        Assert.Equal(ItemCodes.InUse, failure!.Code);
        Assert.Equal((1, 2, 1, 2), await RowsAsync(db, item));
    }

    [Fact]
    public async Task Preview_counts_facts_and_drops_zeroes()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        db.Flows.AddRange(Flow(item, alice, 5), Flow(item, alice, 6));
        await db.SaveChangesAsync(Ct);

        var preview = await scope.ServiceProvider.GetRequiredService<IItemDeletions>().PreviewAsync(item, Ct);

        Assert.Equal([new DeletionCount("flows", 2)], preview!.Removes);
        Assert.Empty(preview.Blockers);
        Assert.False(preview.Imported);
        Assert.Equal("PEA", preview.Name);
        Assert.Equal((await db.Items.AsNoTracking().SingleAsync(i => i.Id == item.Value, Ct)).Version, preview.Version);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IItemDeletions>().PreviewAsync(ItemId.New(), Ct));
    }

    [Fact]
    public async Task Pending_proposals_are_superseded_except_the_one_being_approved()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        var proposals = scope.ServiceProvider.GetRequiredService<IProposalStore>();
        NewProposal Proposal(string asOf) => new("investments", "investments.record_valuation", item, alice, asOf,
            JsonDocument.Parse($$"""{"asOf":"{{asOf}}"}""").RootElement.Clone(), 1, [], "api", null, "Relevé");
        var approving = (await proposals.SubmitAsync(Proposal("2026-01-10"), Ct)).Proposal.Id;
        var other = (await proposals.SubmitAsync(Proposal("2026-01-11"), Ct)).Proposal.Id;
        scope.ServiceProvider.GetRequiredService<IApprovingProposal>().Id = approving;
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
        var preview = await deletions.PreviewAsync(item, Ct);

        Assert.True((await deletions.DeleteAsync(item, preview!.Version, Ct)).IsSuccess);

        Assert.Equal(ProposalStatus.Pending, (await proposals.FindAsync(approving, Ct))!.Status);
        Assert.Equal(ProposalStatus.Superseded, (await proposals.FindAsync(other, Ct))!.Status);
    }
}
