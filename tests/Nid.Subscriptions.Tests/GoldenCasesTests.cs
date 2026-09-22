using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

/// <summary>The worked examples of docs/subscription-data-model.md, with the results that document states.</summary>
public class GoldenCasesTests
{
    private static DateRange Range(LocalDate start, LocalDate? endBefore)
        => DateRange.Create(start, endBefore).Ok();

    [Fact]
    public void Spotify_with_a_price_change_and_a_gift_card()
    {
        var input = Input(
            [Phase(new LocalDate(2024, 6, 28), new LocalDate(2025, 6, 28), 15.17m),
             Phase(new LocalDate(2025, 6, 28), null, 17.20m)],
            [Purchase(new LocalDate(2025, 10, 10), 0m, PurchaseKind.Gift,
                      [Grant(Months(3), CoverageSource.Gift)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 10, 15), new LocalDate(2026, 6, 1)).Ok();

        // "paid until 2025-10-28, gift from 2025-10-28 to 2026-01-28, next charge of 17.20 € on 2026-01-28"
        var gift = Assert.Single(timeline.Coverage, p => p.Source == CoverageSource.Gift);
        Assert.Equal(new LocalDate(2025, 10, 28), gift.Start);
        Assert.Equal(new LocalDate(2026, 1, 28), gift.EndBefore);

        var next = timeline.NextCharge()!;
        Assert.Equal(new LocalDate(2026, 1, 28), next.Date);
        Assert.Equal(17.20m, next.Price.Amount);
    }

    [Fact]
    public void NordVpn_two_year_term_with_bonus_months()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2025, 6, 22), 179.86m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(24), CoverageSource.Paid, new LocalDate(2025, 6, 22)),
                       Grant(Months(6), CoverageSource.Bonus)])]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 7, 1), new LocalDate(2030, 1, 1)).Ok();

        // "Cash cost: 179.86 € on 2025-06-22. Amortized: 179.86 € spread over the ~30 months of coverage."
        var cash = timeline.CashCost(Range(new LocalDate(2025, 6, 22), new LocalDate(2025, 6, 23)));
        Assert.Equal(179.86m, cash.For(Currency.Eur)!.Value.Amount);
        Assert.Equal(new LocalDate(2027, 12, 22), timeline.ExpiresOn());

        var amortized = timeline.AmortizedCost(Range(new LocalDate(2025, 6, 22), new LocalDate(2027, 12, 22)));
        Assert.Equal(179.86m, amortized.For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void A_game_expansion_including_one_month()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2024, 11, 18), 88.99m, PurchaseKind.Bundle,
                      [Grant(Months(1), CoverageSource.Included)], allocated: 0m)]);

        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1)).Ok();

        var window = Range(new LocalDate(2024, 11, 1), new LocalDate(2025, 1, 1));

        // The bundle's full price is real money; none of it counts as subscription cost.
        Assert.Equal(88.99m, timeline.CashCost(window).For(Currency.Eur)!.Value.Amount);
        Assert.Null(timeline.AmortizedCost(window).For(Currency.Eur));
        Assert.Equal(30, timeline.FreeDays(window));
    }
}
