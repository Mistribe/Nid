using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>
/// Pins a deliberate limitation recorded in the 1C1 design (§6) rather than asserting desired behaviour.
/// <c>HouseholdCost.Combine</c> deduplicates <em>cash</em> cost by purchase id: a purchase granting to two
/// subscriptions left the account once, so the household cash total counts it once. It does NOT deduplicate
/// <em>amortized</em> cost, because the engine amortizes per timeline and a shared purchase's days belong to
/// both. So the household amortized total double counts a shared purchase, unlike its cash counterpart.
///
/// The fuller fix — splitting a shared purchase's amortized cost per grant — would change
/// <c>docs/subscription-data-model.md</c>, the 1B contracts and the engine, and is deliberately out of scope
/// for 1C1. This test exists so 1E and 1C2 inherit a known fact rather than a surprise; if it ever starts
/// failing because the double count went away, that is good news — update this comment and the design's
/// Execution notes rather than treating it as a regression to fix blindly.
/// </summary>
[Collection("postgres")]
public class KnownLimitationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Household_amortized_cost_still_double_counts_a_shared_purchase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (first, second) = await app.NewSharedBundleAsync(app.AdultA, 20m);

        var list = await app.GetAsync<ListResponse>(
            app.AdultA, "/api/v1/subscriptions?from=2025-01-01&to=2025-02-01");

        Assert.Single(list.SharedPurchaseIds); // one purchase, granted to both subscriptions

        // Cash cost deduplicates by purchase id: the money left the account once.
        Assert.Equal(20m, list.HouseholdCashCost.Single(m => m.Currency == "EUR").Amount);

        // Amortized cost does not: each of the two subscriptions amortizes its own timeline over the same
        // window, and the shared purchase's days count fully on both, so the household total is double the
        // cash figure above. This asymmetry is the limitation being pinned.
        Assert.Equal(40m, list.HouseholdAmortizedCost.Single(m => m.Currency == "EUR").Amount);

        Assert.NotEqual(first, second);
    }

    public sealed record MoneyResponse(decimal Amount, string Currency);

    public sealed record ListResponse(
        IReadOnlyList<MoneyResponse> HouseholdCashCost,
        IReadOnlyList<MoneyResponse> HouseholdAmortizedCost,
        IReadOnlyList<Guid> SharedPurchaseIds);
}
