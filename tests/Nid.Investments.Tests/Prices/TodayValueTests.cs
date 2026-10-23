using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using EstimatedLine = Nid.Investments.Domain.EstimatedLine;
using NodaTime;

namespace Nid.Investments.Tests.Prices;

/// <summary>Holding prices spec §5.4: the estimate is today's value only when it is newer than the latest valuation.</summary>
public class TodayValueTests
{
    private static readonly LocalDate Valued = new(2026, 9, 30);
    private static readonly Money Zero = M(0m);

    private static Money M(decimal amount) => Money.Create(amount, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();

    private static readonly EnvelopeFigures Figures = new(M(1500m), Valued, null, null, null, null, null, null, null, Zero, Zero, false, null);

    private static EnvelopeEstimate Estimate(decimal? value, LocalDate? asOf)
        => new(ItemId.New(), value is { } v ? M(v) : null, asOf, Valued, false, [], value is null ? "no_newer_price" : null, [new EstimatedLine(Guid.NewGuid(), 1600m)]);

    [Fact]
    public void A_newer_estimate_is_picked_with_its_lines()
    {
        var estimate = Estimate(1600m, Valued.PlusDays(1));
        Assert.Equal((M(1600m), Valued.PlusDays(1), true, estimate.Lines), TodayValue.Pick(Figures, estimate));
    }

    [Fact]
    public void Otherwise_the_valuation_stays()
    {
        Assert.Equal((M(1500m), Valued, false, null), TodayValue.Pick(Figures, Estimate(1600m, Valued)));
        Assert.Equal((M(1500m), Valued, false, null), TodayValue.Pick(Figures, Estimate(null, null)));
        Assert.Equal((M(1500m), Valued, false, null), TodayValue.Pick(Figures, null));
    }
}
