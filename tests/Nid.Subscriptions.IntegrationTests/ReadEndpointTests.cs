using System.Net;
using System.Net.Http.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

[Collection("postgres")]
public class ReadEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_list_shows_a_shared_subscription_to_both_adults()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        foreach (var adult in new[] { app.AdultA, app.AdultB })
        {
            var list = await app.GetAsync<ListResponse>(adult, "/api/v1/subscriptions");
            Assert.Contains(list.Items, i => i.Id == id.Value);
        }
    }

    [Fact]
    public async Task The_list_never_shows_the_other_adults_private_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var privateOfA = await app.NewSubscriptionAsync("Therapy", app.AdultA, Visibility.Private);

        var asB = await app.GetAsync<ListResponse>(app.AdultB, "/api/v1/subscriptions?scope=me");

        Assert.DoesNotContain(asB.Items, i => i.Id == privateOfA.Value);
        Assert.DoesNotContain("Therapy", await app.GetRawAsync(app.AdultB, "/api/v1/subscriptions?scope=me"));
    }

    [Fact]
    public async Task The_household_scope_hides_the_actors_own_private_subscription_too()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var privateOfA = await app.NewSubscriptionAsync("Therapy", app.AdultA, Visibility.Private);

        var household = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=household");
        var me = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me");

        Assert.DoesNotContain(household.Items, i => i.Id == privateOfA.Value);
        Assert.Contains(me.Items, i => i.Id == privateOfA.Value);
    }

    [Fact]
    public async Task The_detail_of_the_other_adults_private_subscription_is_not_found_not_forbidden()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var privateOfA = await app.NewSubscriptionAsync("Therapy", app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/subscriptions/{privateOfA.Value}");

        // Existence is not disclosed: 404, never 403.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_detail_reproduces_the_Spotify_golden_case_over_HTTP()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSpotifyGoldenCaseAsync(app.AdultA);

        var detail = await app.GetAsync<DetailResponse>(
            app.AdultA, $"/api/v1/subscriptions/{id.Value}?horizon=2026-06-01");

        var gift = Assert.Single(detail.Coverage, c => c.Source == "gift");
        Assert.Equal("2025-10-28", gift.Start);
        Assert.Equal("2026-01-28", gift.EndBefore);
        Assert.Equal("2026-01-28", detail.NextChargeOn);
        Assert.Equal(17.20m, detail.NextChargeAmount!.Amount);
    }

    [Fact]
    public async Task An_open_subscription_reports_that_the_walk_stopped_at_the_horizon()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var detail = await app.GetAsync<DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{id.Value}");

        Assert.Contains(detail.Findings, f => f.Code == "subscriptions.horizon_reached");
    }

    [Fact]
    public async Task A_purchase_shared_by_two_subscriptions_counts_once_in_the_household_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (first, second) = await app.NewSharedBundleAsync(app.AdultA, 20m);

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?from=2025-01-01&to=2025-02-01");

        var perSubscription = list.Items.Where(i => i.Id == first.Value || i.Id == second.Value)
            .Sum(i => i.CashCost.Single(m => m.Currency == "EUR").Amount);
        Assert.Equal(40m, perSubscription);                                             // each shows what it cost
        Assert.Equal(20m, list.HouseholdCashCost.Single(m => m.Currency == "EUR").Amount); // the money left once
        Assert.Single(list.SharedPurchaseIds);
    }

    public sealed record MoneyResponse(decimal Amount, string Currency);
    public sealed record FindingResponse(string Code, string Message, string? Reference);
    public sealed record SummaryResponse(Guid Id, string DisplayName, IReadOnlyList<MoneyResponse> CashCost);
    public sealed record ListResponse(
        IReadOnlyList<SummaryResponse> Items,
        IReadOnlyList<MoneyResponse> HouseholdCashCost,
        IReadOnlyList<Guid> SharedPurchaseIds);
    public sealed record CoverageResponse(string Start, string EndBefore, string Source, bool IsProjected);
    public sealed record DetailResponse(
        Guid Id, string DisplayName, string? NextChargeOn, MoneyResponse? NextChargeAmount,
        IReadOnlyList<CoverageResponse> Coverage, IReadOnlyList<FindingResponse> Findings);
}
