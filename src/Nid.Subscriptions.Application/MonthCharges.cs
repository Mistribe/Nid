using System.Globalization;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>The charges of one month (spec §5.1), in the reference currency: null total when a rate is missing, zero when the month is empty.</summary>
public sealed record SubscriptionCharges(
    string Month, MoneyView? Total, bool ConvertedIsEstimate, IReadOnlyList<ChargeLineView> Charges, IReadOnlyList<FindingView> Findings)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Charges.Select(c => c.ItemId).Distinct();
}

public static class MonthCharges
{
    public const string HorizonReachedCode = "subscriptions.horizon_reached";

    /// <summary>
    /// <paramref name="truncated"/>: the month straddles the horizon, its charges stop at it (stated by a finding).
    /// Every visible subscription (ended ones too, for past months): the charges of [1st, 1st of the next month), past and projected.</summary>
    public static SubscriptionCharges Build(
        IReadOnlyList<BuiltSubscription> set, YearMonth month, Currency reference, RateTable rates, bool beyondHorizon, bool truncated = false)
    {
        var label = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var inMonth = ChargeCycles.HouseholdCharges(beyondHorizon ? [] : set).Where(x => InMonth(x.Charge, month))
            .OrderBy(x => x.Charge.Date).ThenBy(x => x.Sub.DisplayName, StringComparer.Ordinal).ToList();
        var converted = FxConversion.Convert(inMonth.Select(x => Piece(x.Charge)), rates, reference);
        var lines = inMonth.Select(x =>
        {
            var one = FxConversion.Convert([Piece(x.Charge)], rates, reference);
            return new ChargeLineView(x.Charge.Date, x.Sub.Input.Subscription.Id, x.Sub.DisplayName, x.Sub.Provider,
                MoneyView.From(x.Charge.Price), one.Total is { } m ? MoneyView.From(m) : null);
        }).ToList();
        return new SubscriptionCharges(label, converted.Total is { } t ? MoneyView.From(t) : null, converted.IsEstimate, lines,
            converted.Missing.Select(FindingView.From).Concat(beyondHorizon
                ? [new FindingView(HorizonReachedCode, "This month is past the furthest date subscriptions are projected to.", null)]
                : truncated ? [new FindingView(HorizonReachedCode, "The forecast stops within this month: charges after that date are not shown.", null)] : []).ToList());
    }

    /// <summary>Every piece <see cref="Build"/> converts, for one rate query.</summary>
    public static IEnumerable<CostPiece> RatePieces(IReadOnlyList<BuiltSubscription> set, YearMonth month)
        => ChargeCycles.HouseholdCharges(set).Where(x => InMonth(x.Charge, month)).Select(x => Piece(x.Charge));

    private static bool InMonth(Charge charge, YearMonth month) => new YearMonth(charge.Date.Year, charge.Date.Month) == month;

    private static CostPiece Piece(Charge charge) => new(charge.Date, charge.Price.Currency, charge.Price.Amount, charge.IsProjected);
}
