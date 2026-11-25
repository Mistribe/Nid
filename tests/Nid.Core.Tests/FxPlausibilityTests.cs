using Nid.Core.Contracts;
using Nid.Core.Domain.Fx;
using NodaTime;

namespace Nid.Core.Tests;

public class FxPlausibilityTests
{
    private static readonly LocalDate Day = new(2026, 9, 20);
    private static readonly Currency Gbp = Currency.TryCreate("GBP", out var c) ? c : default;

    private static FxRate Rate(Currency quote, decimal rate) => new(Day, quote, rate, FxRateSource.SubTrackerData);

    private static readonly Dictionary<string, decimal> NoRefusal = [];

    [Fact]
    public void A_level_refused_the_day_before_and_confirmed_is_written()
    {
        // #633: −40 % two days in a row is a lasting move; a third level far from both stays refused.
        var previous = new Dictionary<Currency, decimal> { [Gbp] = 1m };
        var refused = new Dictionary<string, decimal> { ["GBP"] = 0.6m };

        Assert.Single(FxPlausibility.Check([Rate(Gbp, 0.62m)], previous, refused, 0.25m).Rates);
        Assert.Single(FxPlausibility.Check([Rate(Gbp, 0.3m)], previous, refused, 0.25m).Rejected);
    }

    [Fact]
    public void A_change_of_24_percent_is_written_and_26_percent_refused_for_that_quote_only()
    {
        var previous = new Dictionary<Currency, decimal> { [Currency.Eur] = 1m, [Gbp] = 1m };

        var checkedRates = FxPlausibility.Check([Rate(Currency.Eur, 1.24m), Rate(Gbp, 1.26m)], previous, NoRefusal, 0.25m);

        Assert.Equal(Currency.Eur, Assert.Single(checkedRates.Rates).Quote);
        var rejected = Assert.Single(checkedRates.Rejected);
        Assert.Equal(("GBP", 1m, FxCodes.ImplausibleChange), (rejected.Quote, rejected.Previous, rejected.Reason));
    }

    [Fact]
    public void A_fall_is_measured_the_same_way()
        => Assert.Single(FxPlausibility.Check([Rate(Currency.Eur, 0.74m)], new Dictionary<Currency, decimal> { [Currency.Eur] = 1m }, NoRefusal, 0.25m).Rejected);

    [Fact]
    public void A_quote_with_nothing_to_compare_with_is_written()
        => Assert.Single(FxPlausibility.Check([Rate(Currency.Eur, 1000m)], new Dictionary<Currency, decimal>(), NoRefusal, 0.25m).Rates);
}
