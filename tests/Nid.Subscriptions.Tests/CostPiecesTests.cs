using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

/// <summary>The pieces carry the date of the charge they come from; summed, they are exactly today's totals.</summary>
public class CostPiecesTests
{
    private static DateRange Range(LocalDate start, LocalDate? endBefore) => DateRange.Create(start, endBefore).Ok();

    private static decimal Sum(IEnumerable<CostPiece> pieces, Currency currency)
        => pieces.Where(p => p.Currency == currency).Sum(p => p.Amount);

    [Fact]
    public void NordVpn_amortized_pieces_all_carry_the_purchase_date_and_sum_to_the_total()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2025, 6, 22), 179.86m, PurchaseKind.PrepaidTerm,
                      [Grant(Months(24), CoverageSource.Paid, new LocalDate(2025, 6, 22)),
                       Grant(Months(6), CoverageSource.Bonus)])]);
        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 7, 1), new LocalDate(2030, 1, 1)).Ok();
        var window = Range(new LocalDate(2025, 9, 1), new LocalDate(2025, 10, 1));

        var pieces = timeline.AmortizedCostPieces(window);

        Assert.All(pieces, p => Assert.Equal(new LocalDate(2025, 6, 22), p.ChargeDate));
        Assert.Equal(timeline.AmortizedCost(window).For(Currency.Eur)!.Value.Amount, Sum(pieces, Currency.Eur));
    }

    [Fact]
    public void Spotify_cash_pieces_are_one_per_charge_in_the_window_and_flag_projections()
    {
        var input = Input(
            [Phase(new LocalDate(2024, 6, 28), new LocalDate(2025, 6, 28), 15.17m),
             Phase(new LocalDate(2025, 6, 28), null, 17.20m)],
            [Purchase(new LocalDate(2025, 10, 10), 0m, PurchaseKind.Gift, [Grant(Months(3), CoverageSource.Gift)])]);
        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 10, 15), new LocalDate(2026, 6, 1)).Ok();
        var window = Range(new LocalDate(2025, 1, 1), new LocalDate(2026, 3, 1));

        var pieces = timeline.CashCostPieces(window);

        Assert.Equal(timeline.CashCost(window).For(Currency.Eur)!.Value.Amount, Sum(pieces, Currency.Eur));
        Assert.Contains(pieces, p => p.ChargeDate == new LocalDate(2026, 1, 28) && p.IsProjected && p.Amount == 17.20m);
        Assert.DoesNotContain(timeline.CashCostPieces(window, includeProjected: false), p => p.IsProjected);
    }

    [Fact]
    public void A_bundle_with_nothing_allocated_has_cash_pieces_and_no_amortized_piece()
    {
        var input = Input(
            [],
            [Purchase(new LocalDate(2024, 11, 18), 88.99m, PurchaseKind.Bundle, [Grant(Months(1), CoverageSource.Included)], allocated: 0m)]);
        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1)).Ok();
        var window = Range(new LocalDate(2024, 11, 1), new LocalDate(2025, 1, 1));

        Assert.Equal(88.99m, Assert.Single(timeline.CashCostPieces(window)).Amount);
        Assert.Empty(timeline.AmortizedCostPieces(window));
    }

    [Fact]
    public void Pieces_keep_their_currency()
    {
        var usd = Money.Create(9.99m, Currency.Usd).Ok();
        var input = Input(
            [new BillingPhase(BillingPhaseId.New(), new LocalDate(2025, 1, 1), new LocalDate(2025, 3, 1), usd, Months(1), null, null, Household)],
            []);
        var timeline = SubscriptionTimeline.Build(input, new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1)).Ok();

        var pieces = timeline.CashCostPieces(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 3, 1)));

        Assert.Equal([new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1)], pieces.Select(p => p.ChargeDate).ToList());
        Assert.All(pieces, p => Assert.Equal(Currency.Usd, p.Currency));
    }
}
