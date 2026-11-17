using Nid.Childcare.Application;
using Nid.Core.Contracts;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

/// <summary>#94: a nanny at home's year from its months typed as paid.</summary>
public class NannyYearTests
{
    private static readonly ItemId Item = new(Guid.NewGuid());

    private static NannyMonth Month(int y, int m, decimal paid, decimal cmg) => new(Item, new YearMonth(y, m), paid, cmg);

    private static readonly NannyMonth[] Months = [Month(2025, 12, 900m, 300m), Month(2026, 1, 1000m, 400m), Month(2026, 3, 1100m, 400m)];

    [Fact]
    public void Totals_sum_the_year_s_months_only()
        => Assert.Equal(new NannyYear.Totals(2100m, 800m, 1300m, 2), NannyYear.Sum(NannyYear.Of(Months, 2026)));

    [Fact]
    public void The_rest_of_the_current_year_counts_at_the_mean_net_cost()
    {
        var projected = NannyYear.Projected(Months, 2026, D(2026, 5, 10), closed: false);

        // After March, the latest recorded month, to December: 9 months at (600 + 700) / 2.
        Assert.Equal(Enumerable.Range(4, 9).Select(m => new YearMonth(2026, m)), projected.Select(p => p.Month));
        Assert.All(projected, p => Assert.Equal(650m, p.Amount));
    }

    [Fact]
    public void Nothing_is_projected_for_another_year_without_months_or_once_closed()
    {
        Assert.Empty(NannyYear.Projected(Months, 2025, D(2026, 5, 10), closed: false));
        Assert.Empty(NannyYear.Projected(Months, 2027, D(2027, 5, 10), closed: false));
        Assert.Empty(NannyYear.Projected(Months, 2026, D(2026, 5, 10), closed: true));
        Assert.Empty(NannyYear.Projected([Month(2026, 12, 1000m, 0m)], 2026, D(2026, 12, 20), closed: false));
    }

    [Fact]
    public void Years_are_those_with_a_month_and_the_current_one_newest_first()
        => Assert.Equal([2027, 2026, 2025], NannyYear.Years(Months, D(2027, 1, 5)));
}
