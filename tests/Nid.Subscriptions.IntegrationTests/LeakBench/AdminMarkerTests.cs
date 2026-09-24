using Nid.Core.Application.Fx;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

/// <summary>
/// The admin area is admin-only by a marker, so forgetting the marker is the one way to open it: every request in
/// its two namespaces, and every rate request but the reading one, must carry it (authorization.md §4).
/// </summary>
public class AdminMarkerTests
{
    private static readonly string[] AdminNamespaces = ["Nid.Core.Application.Admin", "Nid.Platform.Admin"];

    [Fact]
    public void Every_request_of_the_admin_area_and_every_rate_write_is_admin_only()
    {
        var adminArea = RequestCatalogue.Discover().Where(t => AdminNamespaces.Contains(t.Namespace)).ToList();
        Type[] rateRequests =
        [
            typeof(SetManualFxRateCommand), typeof(RequestFxSyncCommand), typeof(GetFxSyncRunsQuery),
            typeof(CollectDailyFxRatesCommand), typeof(BackfillFxRatesCommand),
        ];

        Assert.NotEmpty(adminArea);
        var unmarked = adminArea.Concat(rateRequests).Where(t => !typeof(IAdminOnly).IsAssignableFrom(t)).ToList();
        Assert.True(unmarked.Count == 0, "Not IAdminOnly:\n  " + string.Join("\n  ", unmarked.Select(t => t.FullName)));

        // Every adult's totals read the rates: reading them stays open.
        Assert.False(typeof(IAdminOnly).IsAssignableFrom(typeof(GetFxRatesQuery)));
    }

    /// <summary>1C2b spec §6: the sync journal and the sync are the admin's; they live outside the admin namespaces, so they are named here.</summary>
    [Fact]
    public void The_catalog_sync_journal_is_admin_only()
    {
        Type[] journal = [typeof(SyncCatalogCommand), typeof(RequestCatalogSyncCommand), typeof(GetCatalogRunsQuery)];
        var unmarked = journal.Where(t => !typeof(IAdminOnly).IsAssignableFrom(t)).ToList();
        Assert.True(unmarked.Count == 0, "Not IAdminOnly:\n  " + string.Join("\n  ", unmarked.Select(t => t.FullName)));
    }

    [Fact]
    public void No_admin_only_request_touches_an_item()
    {
        var touching = RequestCatalogue.Discover()
            .Where(t => typeof(IAdminOnly).IsAssignableFrom(t))
            .Where(t => typeof(IItemScoped).IsAssignableFrom(t) || typeof(ICreatesItem).IsAssignableFrom(t))
            .ToList();

        Assert.True(touching.Count == 0, "Admin-only requests must not touch items:\n  " + string.Join("\n  ", touching.Select(t => t.FullName)));
    }
}
