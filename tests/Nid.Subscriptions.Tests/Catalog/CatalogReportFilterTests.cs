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

    /// <summary>
    /// Final review H1: a person's visible set is always computed, whatever the runs hold, so the time
    /// the journal takes says nothing about whether the household ever had a household entry.
    /// </summary>
    [Fact]
    public async Task A_persons_visible_set_is_always_computed()
    {
        var visible = new CountingVisibleItems();

        await CatalogReportFilter.ForAsync(Actor.Browser(PersonId.New()), visible, TestContext.Current.CancellationToken);

        Assert.Equal(1, visible.Calls);
    }

    [Fact]
    public async Task The_system_actor_computes_no_visible_set()
    {
        var visible = new CountingVisibleItems();
        await CatalogReportFilter.ForAsync(Actor.System, visible, TestContext.Current.CancellationToken);
        Assert.Equal(0, visible.Calls);
    }
}
