using Nid.Core.Application.Fx;
using Nid.Investments.Application.Prices;
using Nid.RealEstate.Application.Irl;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;

namespace Nid.Host.IntegrationTests.LeakBench;

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

    /// <summary>Holding prices spec §6: the collection, its trigger and its journal are the admin's.</summary>
    [Fact]
    public void The_price_collection_is_admin_only()
    {
        Type[] prices = [typeof(CollectPricesCommand), typeof(RequestPriceSyncCommand), typeof(GetPriceRunsQuery)];
        Assert.DoesNotContain(prices, t => !typeof(IAdminOnly).IsAssignableFrom(t));
    }

    /// <summary>Bank sync spec §2.8: the nightly sync and its journal are the admin's; the person's own sync is not.</summary>
    [Fact]
    public void The_bank_sync_journal_is_admin_only()
    {
        Type[] journal = [typeof(Nid.Banking.Application.Sync.SyncAllCommand), typeof(Nid.Banking.Application.Sync.GetBankSyncRunsQuery)];
        Assert.DoesNotContain(journal, t => !typeof(IAdminOnly).IsAssignableFrom(t));
        Assert.False(typeof(IAdminOnly).IsAssignableFrom(typeof(Nid.Banking.Application.Sync.SyncMineCommand)));
    }

    /// <summary>Rental spec §3.1: the IRL collection, its trigger, its journal and the manual entry are the admin's.</summary>
    [Fact]
    public void The_irl_collection_is_admin_only()
    {
        Type[] irl = [typeof(CollectIrlCommand), typeof(RequestIrlSyncCommand), typeof(GetIrlAdminQuery), typeof(GetIrlRunsQuery), typeof(SetIrlValueCommand)];
        Assert.DoesNotContain(irl, t => !typeof(IAdminOnly).IsAssignableFrom(t));
    }

    /// <summary>The CPI collection, its trigger, its journal and the manual entry are the admin's; the read of the inflation is not.</summary>
    [Fact]
    public void The_cpi_collection_is_admin_only()
    {
        Type[] cpi = [typeof(Nid.Core.Application.Inflation.CollectCpiCommand), typeof(Nid.Core.Application.Inflation.RequestCpiSyncCommand),
            typeof(Nid.Core.Application.Inflation.GetCpiQuery), typeof(Nid.Core.Application.Inflation.SetCpiValueCommand)];
        Assert.DoesNotContain(cpi, t => !typeof(IAdminOnly).IsAssignableFrom(t));
        Assert.False(typeof(IAdminOnly).IsAssignableFrom(typeof(Nid.Core.Application.Inflation.GetInflationQuery)));
    }

    /// <summary>Adding a subscription needs them, so no admin marker may creep onto them.</summary>
    [Fact]
    public void The_provider_requests_stay_open_to_every_adult()
    {
        Type[] open = [typeof(ListProvidersQuery), typeof(ListLabelsQuery), typeof(CreateProviderCommand), typeof(GetProviderIconQuery)];
        Assert.DoesNotContain(open, t => typeof(IAdminOnly).IsAssignableFrom(t));
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
