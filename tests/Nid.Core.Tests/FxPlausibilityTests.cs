using Nid.Core.Contracts;
using Nid.Core.Domain.Fx;
using NodaTime;

namespace Nid.Core.Tests;

public class FxPlausibilityTests
{
    private static readonly LocalDate Day = new(2026, 9, 20);
    private static readonly Currency Gbp = Currency.TryCreate("GBP", out var c) ? c : default;

    private static FxRate Rate(Currency quote, decimal rate) => new(Day, quote, rate, FxRateSource.SubTrackerData);

    [Fact]
    public void A_change_of_24_percent_is_written_and_26_percent_refused_for_that_quote_only()
    {
        var previous = new Dictionary<Currency, decimal> { [Currency.Eur] = 1m, [Gbp] = 1m };

        var checkedRates = FxPlausibility.Check([Rate(Currency.Eur, 1.24m), Rate(Gbp, 1.26m)], previous, 0.25m);

        Assert.Equal(Currency.Eur, Assert.Single(checkedRates.Rates).Quote);
        var rejected = Assert.Single(checkedRates.Rejected);
        Assert.Equal(("GBP", 1m, FxCodes.ImplausibleChange), (rejected.Quote, rejected.Previous, rejected.Reason));
    }

    [Fact]
    public void A_fall_is_measured_the_same_way()
        => Assert.Single(FxPlausibility.Check([Rate(Currency.Eur, 0.74m)], new Dictionary<Currency, decimal> { [Currency.Eur] = 1m }, 0.25m).Rejected);

    [Fact]
    public void A_quote_with_nothing_to_compare_with_is_written()
        => Assert.Single(FxPlausibility.Check([Rate(Currency.Eur, 1000m)], new Dictionary<Currency, decimal>(), 0.25m).Rates);
}
