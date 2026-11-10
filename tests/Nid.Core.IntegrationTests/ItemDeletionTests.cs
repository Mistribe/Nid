using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;

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

    /// <summary>Registered after Core's part: counts the item's flows when its own remove runs.</summary>
    private sealed class ReadingPart(CoreDbContext db) : IItemDeletionPart
    {
        public static int? FlowsSeen;

        public Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionCount>>([]);

        public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

        public Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct) => Task.FromResult(false);

        public async Task RemoveAsync(ItemAccess item, CancellationToken ct) => FlowsSeen = await db.Flows.CountAsync(f => f.ItemId == item.Id.Value, ct);
    }

    private async Task<ServiceProvider> ProviderAsync(bool blocked = false, bool reading = false)
    {
        var services = new ServiceCollection();
        services.AddNid(await postgres.NewConnectionStringAsync());
        if (blocked)
            services.AddScoped<IItemDeletionPart, BlockingPart>();
        if (reading)
            services.AddScoped<IItemDeletionPart, ReadingPart>();
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

    private static async Task<ItemDeletionPreview> Preview(IItemDeletions deletions, ItemId item)
        => (await deletions.PreviewAsync(item, "investments", null, Ct)).Ok();

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
        var preview = await Preview(deletions, item);

        Assert.True((await deletions.DeleteAsync(item, "investments", null, preview.Version, Ct)).IsSuccess);

        Assert.Equal((0, 0, 0, 0), await RowsAsync(db, item));
    }

    [Fact]
    public async Task Core_part_runs_last_whatever_the_registration_order()
    {
        await using var provider = await ProviderAsync(reading: true);
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        await AddFactsAsync(scope.ServiceProvider.GetRequiredService<CoreDbContext>(), item, alice);
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
        var preview = await Preview(deletions, item);
        ReadingPart.FlowsSeen = null;

        Assert.True((await deletions.DeleteAsync(item, "investments", null, preview.Version, Ct)).IsSuccess);

        Assert.Equal(1, ReadingPart.FlowsSeen);
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
        var preview = await Preview(deletions, item);
        var items = scope.ServiceProvider.GetRequiredService<IItemRegistry>();
        var found = (await items.FindAsync(item, Ct))!;
        Assert.True((await items.ReplaceAsync(item, "PEA Alice", found.Visibility,
            found.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), Ct)).IsSuccess);

        Assert.True((await deletions.DeleteAsync(item, "investments", null, preview.Version, Ct)).TryGetFailure(out var failure));

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
        var preview = await Preview(deletions, item);
        Assert.Equal("goal_funding", Assert.Single(preview.Blockers).Key);

        Assert.True((await deletions.DeleteAsync(item, "investments", null, preview.Version, Ct)).TryGetFailure(out var failure));

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

        var preview = await Preview(scope.ServiceProvider.GetRequiredService<IItemDeletions>(), item);

        Assert.Equal([new DeletionCount("flows", 2)], preview.Removes);
        Assert.Empty(preview.Blockers);
        Assert.False(preview.Imported);
        Assert.Equal("PEA", preview.Name);
        Assert.Equal((await db.Items.AsNoTracking().SingleAsync(i => i.Id == item.Value, Ct)).Version, preview.Version);
        Assert.IsType<NotFoundFailure>((await scope.ServiceProvider.GetRequiredService<IItemDeletions>().PreviewAsync(ItemId.New(), "investments", null, Ct)).Fails());
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
        var preview = await Preview(deletions, item);

        Assert.True((await deletions.DeleteAsync(item, "investments", null, preview.Version, Ct)).IsSuccess);

        Assert.Equal(ProposalStatus.Pending, (await proposals.FindAsync(approving, Ct))!.Status);
        Assert.Equal(ProposalStatus.Superseded, (await proposals.FindAsync(other, Ct))!.Status);
    }

    [Fact]
    public async Task Preview_counts_only_active_facts_and_the_delete_takes_the_retired_ones_too()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var (alice, item) = await SeedAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        await AddFactsAsync(db, item, alice);
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();

        var preview = await Preview(deletions, item);

        Assert.Equal([new DeletionCount("flows", 1), new DeletionCount("valuations", 1)], preview.Removes);
        Assert.True((await deletions.DeleteAsync(item, "investments", null, preview.Version, Ct)).IsSuccess);
        Assert.Equal((0, 0, 0, 0), await RowsAsync(db, item));
    }

    [Fact]
    public async Task Another_module_or_kind_answers_not_found_and_keeps_everything()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var (_, item) = await SeedAsync(scope.ServiceProvider);
        var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
        var version = (await Preview(deletions, item)).Version;

        Assert.IsType<NotFoundFailure>((await deletions.PreviewAsync(item, "goals", null, Ct)).Fails());
        Assert.IsType<NotFoundFailure>((await deletions.PreviewAsync(item, "investments", "holding", Ct)).Fails());
        Assert.True((await deletions.DeleteAsync(item, "goals", null, version, Ct)).TryGetFailure(out var failure));
        Assert.IsType<NotFoundFailure>(failure);
        Assert.True((await deletions.PreviewAsync(item, "investments", "envelope", Ct)).IsSuccess);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Items.CountAsync(i => i.Id == item.Value, Ct));
    }

    /// <summary>
    /// Nobody approving (a plain delete): every pending proposal is superseded. Its notice resolves once the delete commits, and stays
    /// open when the delete ran inside a unit of work that rolls back (a proposal's dry run). The whole app: the notifier builds every store.
    /// </summary>
    [Fact]
    public async Task Superseded_notices_resolve_only_once_the_delete_commits()
    {
        await using var app = await NidApp.StartAsync(postgres);
        var item = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        Guid proposal;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            proposal = (await scope.ServiceProvider.GetRequiredService<IProposalStore>().SubmitAsync(new NewProposal("investments",
                "investments.record_valuation", item, app.AdultA, "2026-01-10", JsonDocument.Parse("""{"asOf":"2026-01-10"}""").RootElement.Clone(),
                1, [], "api", null, "Relevé"), Ct)).Proposal.Id;
            await scope.ServiceProvider.GetRequiredService<INotifier>().PublishAsync(new Notice("proposal_pending", new Recipients.People([app.AdultA]),
                ProposalNotices.Key(proposal), [item], new Dictionary<string, string>()), Ct);
        }
        Assert.Equal(NotificationStates.Open, await NoticeStateAsync(app, proposal));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
            var version = (await Preview(deletions, item)).Version;
            await using (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(Ct))
                Assert.True((await deletions.DeleteAsync(item, "investments", null, version, Ct)).IsSuccess);
        }
        Assert.Equal(NotificationStates.Open, await NoticeStateAsync(app, proposal));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var deletions = scope.ServiceProvider.GetRequiredService<IItemDeletions>();
            var proposals = scope.ServiceProvider.GetRequiredService<IProposalStore>();
            Assert.Equal(ProposalStatus.Pending, (await proposals.FindAsync(proposal, Ct))!.Status);
            Assert.True((await deletions.DeleteAsync(item, "investments", null, (await Preview(deletions, item)).Version, Ct)).IsSuccess);
            Assert.Equal(ProposalStatus.Superseded, (await proposals.FindAsync(proposal, Ct))!.Status);
        }
        Assert.Equal(NotificationStates.Resolved, await NoticeStateAsync(app, proposal));
    }

    private static async Task<string> NoticeStateAsync(NidApp app, Guid proposal)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var key = ProposalNotices.Key(proposal);
        return (await platform.Notifications.AsNoTracking().SingleAsync(n => n.Key == key, Ct)).State;
    }
}
