using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using NodaTime;

namespace Nid.Subscriptions.Tests.Catalog;

public class CatalogReportFilterTests
{
    private sealed class CountingVisibleItems : IVisibleItems
    {
        public int Calls { get; private set; }

        Task<IReadOnlySet<ItemId>> IVisibleItems.ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
            => ForAsync(actor, scope, includeArchived: false, ct);

        public Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlySet<ItemId>>(new HashSet<ItemId>());
        }
    }

    private static CatalogSyncRun Run(CatalogReport? report)
        => new(Guid.CreateVersion7(), Instant.FromUtc(2026, 9, 24, 7, 0), null, CatalogRunStatus.Succeeded, "c1", null, null, report);

    /// <summary>
    /// Final review H1: a person's visible set is computed whether or not a run holds a household entry, so the time
    /// the journal takes says nothing about whether the household ever had one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_persons_visible_set_is_computed_whether_or_not_a_household_entry_exists(bool household)
    {
        var visible = new CountingVisibleItems();
        var report = CatalogReport.Empty with { ProvidersAdded = ["netflix"], AdoptedRemaining = household ? ["wow"] : [] };

        await CatalogReportFilter.ForAsync(Actor.Browser(PersonId.New()), [Run(report), Run(null)], visible, default);

        Assert.Equal(1, visible.Calls);
    }

    [Fact]
    public async Task The_system_actor_computes_no_visible_set()
    {
        var visible = new CountingVisibleItems();
        await CatalogReportFilter.ForAsync(Actor.System, [Run(CatalogReport.Empty with { AdoptedRemaining = ["wow"] })], visible, default);
        Assert.Equal(0, visible.Calls);
    }
}
