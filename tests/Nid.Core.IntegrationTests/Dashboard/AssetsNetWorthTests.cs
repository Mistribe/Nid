using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>Issue #186: assets in the net worth (other, locked), its change since 1 January and the month's causes. Today is 2025-10-15.</summary>
public class AssetsNetWorthTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/assets";

    private static async Task<Guid> NewAssetAsync(
        NidApp app, string name, string? price, string? on, string visibility = "shared", PersonId? owner = null, string? value = null, string? valueOn = null)
    {
        var response = await app.RawAsync(owner ?? app.AdultA, HttpMethod.Post, Route, new Dictionary<string, object?>
        {
            ["name"] = name, ["kind"] = "art", ["purchasePrice"] = price, ["purchasedOn"] = on, ["currentValue"] = value, ["currentValueOn"] = valueOn,
            ["visibility"] = visibility, ["owners"] = new[] { new { personId = (owner ?? app.AdultA).Value, shareBp = 10_000 } },
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task ValueAsync(NidApp app, Guid id, string asOf, string amount)
        => Assert.Equal(System.Net.HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id}/valuations", new { asOf, amount })).StatusCode);

    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    private static string Amount(JsonElement money) => money.GetProperty("amount").GetString()!;

    private static async Task<IReadOnlyList<NetWorthPoint>> HistoryAsync(NidApp app, params string[] dates)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var source = scope.ServiceProvider.GetServices<INetWorthContribution>().Single(c => c.Module == "assets");
        var history = await source.NetWorthHistoryAsync(app.AdultA, VisibilityScope.Household,
            dates.Select(d => LocalDatePattern.Iso.Parse(d).Value).ToList(), TestContext.Current.CancellationToken);
        Assert.True(history.TryGetValue(out var points));
        return points!;
    }

    private static async Task ArchiveAsync(NidApp app, Guid id, int year, int month, int day)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
            .ArchiveAsync(new ItemId(id), Instant.FromUtc(year, month, day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);
    }

    private static decimal Eur(IReadOnlyDictionary<Currency, decimal>? sums) => sums?.GetValueOrDefault(Currency.Eur) ?? 0m;

    private static Dictionary<string, decimal> Parts(NetWorthPoint before, NetWorthPoint now)
        => MonthChanges.NetWorthParts("assets", before, now).ToDictionary(p => p.Key, p => Eur(p.Now) - Eur(p.Before));

    [Fact]
    public async Task Asset_counts_in_net_worth_as_other_locked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAssetAsync(app, "Tableau", "1000", "2024-06-01", value: "1500", valueOn: "2025-03-01");

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("1500.00", Total(view.GetProperty("netWorth")));
        var allocation = view.GetProperty("allocation");
        Assert.Equal("1500.00", Amount(allocation.GetProperty("classes").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "other").GetProperty("amount")));
        Assert.Equal("1500.00", Amount(allocation.GetProperty("liquidity").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "locked").GetProperty("amount")));
        Assert.Equal("0.00", Amount(allocation.GetProperty("approximate")));
        var row = Assert.Single(view.GetProperty("envelopeSplit").EnumerateArray());
        Assert.Equal(("assets", id, "2025-03-01"), (row.GetProperty("module").GetString(), row.GetProperty("id").GetGuid(), row.GetProperty("asOf").GetString()));
    }

    [Fact]
    public async Task Purchase_counts_as_moved_not_gain()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAssetAsync(app, "Montre", "2000", "2025-04-01");

        var view = await DashboardAsync(app, app.AdultA);
        var points = await HistoryAsync(app, "2025-03-31", "2025-10-15");

        Assert.Equal("2000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal("0.00", Total(view.GetProperty("change")));
        Assert.Equal(new Dictionary<string, decimal> { ["assets.paid_in"] = 0m, ["assets.moved"] = 2_000m, ["assets.value"] = 0m }, Parts(points[0], points[1]));
    }

    [Fact]
    public async Task Valuation_change_is_a_gain()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAssetAsync(app, "Tableau", "1000", "2024-06-01");
        await ValueAsync(app, id, "2025-03-01", "1500");

        var view = await DashboardAsync(app, app.AdultA);
        var points = await HistoryAsync(app, "2024-12-31", "2025-10-15");

        Assert.Equal("500.00", Total(view.GetProperty("change")));
        Assert.Equal("0.00", Total(view.GetProperty("changeContributions")));
        Assert.Equal(new Dictionary<string, decimal> { ["assets.paid_in"] = 0m, ["assets.moved"] = 0m, ["assets.value"] = 500m }, Parts(points[0], points[1]));
    }

    [Fact]
    public async Task Archived_asset_leaves_as_a_sale()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAssetAsync(app, "Tableau", "1000", "2024-06-01");
        await ValueAsync(app, id, "2025-03-01", "1500");
        await ArchiveAsync(app, id, 2025, 6, 15);

        var points = await HistoryAsync(app, "2025-06-14", "2025-06-15");
        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal((1_500m, 1, 1_000m), (Eur(points[0].Values), points[0].Envelopes, Eur(points[0].Moved)));
        Assert.Equal((0m, 0, 1_000m - 1_500m), (Eur(points[1].Values), points[1].Envelopes, Eur(points[1].Moved)));
        Assert.Equal(new Dictionary<string, decimal> { ["assets.paid_in"] = 0m, ["assets.moved"] = -1_500m, ["assets.value"] = 0m }, Parts(points[0], points[1]));
        Assert.Empty(view.GetProperty("envelopeSplit").EnumerateArray());
    }

    [Fact]
    public async Task Private_asset_counts_only_for_its_owner()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAssetAsync(app, "Bague", "3000", "2024-06-01", "private");

        Assert.Equal("3000.00", Total((await DashboardAsync(app, app.AdultA, "me")).GetProperty("netWorth")));
        Assert.Null(Total((await DashboardAsync(app, app.AdultB, "me")).GetProperty("netWorth")));
        Assert.Null(Total((await DashboardAsync(app, app.AdultB, "household")).GetProperty("netWorth")));
    }

    [Fact]
    public async Task Projection_keeps_asset_flat()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAssetAsync(app, "Tableau", "5000", "2024-06-01");

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/projection?horizon=5y");
        var view = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var points = view.GetProperty("points").EnumerateArray().ToList();

        Assert.Equal("5000.00", points[0].GetProperty("nominal").GetString());
        Assert.DoesNotContain(view.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "savings_exhausted");
        Assert.Equal("5000.00", points[^1].GetProperty("nominal").GetString());
    }

    [Fact]
    public async Task A_price_alone_is_no_estimate_and_no_date_of_values()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAssetAsync(app, "Voiture", "8000", "2020-03-01");

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("8000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("valuesAsOf").ValueKind);
        Assert.Equal(0, view.GetProperty("estimatedEnvelopes").GetInt32());
        Assert.Equal(0, view.GetProperty("envelopesWithoutValue").GetInt32());
        var row = Assert.Single(view.GetProperty("envelopeSplit").EnumerateArray());
        Assert.Equal("2020-03-01", row.GetProperty("asOf").GetString());
        Assert.False(row.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task Bought_this_year_then_revalued_the_gain_is_the_revaluation_over_the_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAssetAsync(app, "Montre", "2000", "2025-04-01");
        await ValueAsync(app, id, "2025-09-01", "2600");

        var view = await DashboardAsync(app, app.AdultA);
        var points = await HistoryAsync(app, "2025-03-31", "2025-10-15");

        Assert.Equal("600.00", Total(view.GetProperty("change")));
        Assert.Equal(new Dictionary<string, decimal> { ["assets.paid_in"] = 0m, ["assets.moved"] = 2_000m, ["assets.value"] = 600m }, Parts(points[0], points[1]));
    }

    [Fact]
    public async Task Counted_exactly_on_1_January_starts_at_its_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAssetAsync(app, "Montre", "2000", "2025-01-01");
        await ValueAsync(app, id, "2025-06-01", "2300");

        Assert.Equal("300.00", Total((await DashboardAsync(app, app.AdultA)).GetProperty("change")));
    }

    [Fact]
    public async Task A_valuation_before_the_purchase_moves_that_valuation_on_its_date_and_the_value_never_jumps()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAssetAsync(app, "Montre", "2000", "2025-04-01");
        await ValueAsync(app, id, "2025-02-01", "1800");

        var points = await HistoryAsync(app, "2025-01-31", "2025-02-01", "2025-04-01", "2025-10-15");

        Assert.Equal((0m, 0m), (Eur(points[0].Values), Eur(points[0].Moved)));
        Assert.Equal((1_800m, 1_800m), (Eur(points[1].Values), Eur(points[1].Moved)));
        Assert.Equal((1_800m, 1_800m), (Eur(points[2].Values), Eur(points[2].Moved))); // the purchase day changes nothing
        Assert.Equal((1_800m, 1_800m), (Eur(points[3].Values), Eur(points[3].Moved)));
    }

    private static async Task<Guid> NewVehicleAsync(NidApp app, string price, string on, decimal? rate)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Route, new Dictionary<string, object?>
        {
            ["name"] = "Citadine", ["kind"] = "vehicle", ["purchasePrice"] = price, ["purchasedOn"] = on, ["depreciationRate"] = rate,
            ["visibility"] = "shared", ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_depreciated_vehicle_is_an_estimate_dated_by_its_base()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewVehicleAsync(app, "10000", "2024-10-15", 20m);

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("8000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(1, view.GetProperty("estimatedEnvelopes").GetInt32());
        var row = Assert.Single(view.GetProperty("envelopeSplit").EnumerateArray());
        Assert.Equal("2024-10-15", row.GetProperty("asOf").GetString());
        Assert.True(row.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task A_vehicle_without_a_rule_stays_a_bare_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewVehicleAsync(app, "10000", "2024-10-15", null);

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("10000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(0, view.GetProperty("estimatedEnvelopes").GetInt32());
    }

    [Fact]
    public async Task Depreciation_history_declines_and_moved_stays_the_first_counted_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewVehicleAsync(app, "10000", "2024-10-15", 20m);

        var points = await HistoryAsync(app, "2024-10-14", "2024-10-15", "2025-10-15");

        Assert.Equal((0m, 0m), (Eur(points[0].Values), Eur(points[0].Moved)));
        Assert.Equal((10_000m, 10_000m), (Eur(points[1].Values), Eur(points[1].Moved)));
        Assert.Equal((8_000m, 10_000m), (Eur(points[2].Values), Eur(points[2].Moved)));
        Assert.Equal(new Dictionary<string, decimal> { ["assets.paid_in"] = 0m, ["assets.moved"] = 0m, ["assets.value"] = -2_000m }, Parts(points[1], points[2]));
    }
}
