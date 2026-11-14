using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class LegalMinimumsTests
{
    [Fact]
    public void Terms_meeting_the_minimums_raise_nothing() => Assert.Empty(LegalMinimums.Check(3.50m, 4.50m, 4.00m, D(2026, 11, 2)));

    [Fact]
    public void Exactly_at_the_minimums_raises_nothing()
    {
        Assert.Empty(LegalMinimums.Check(3.28m, null, 3.92m, D(2026, 6, 1)));
        Assert.Empty(LegalMinimums.Check(0m, 4.20m, 3.92m, D(2026, 6, 1)));
    }

    [Fact]
    public void Revaluation_breaks_terms_that_met_the_former_minimums()
    {
        Assert.Empty(LegalMinimums.Check(3.00m, null, 3.83m, D(2026, 5, 31)));
        var after = LegalMinimums.Check(3.00m, null, 3.83m, D(2026, 6, 1));
        Assert.Equal([new(LegalMinimums.Codes.NetRate, D(2026, 6, 1), 3.28m, 3.00m), new(LegalMinimums.Codes.Upkeep, D(2026, 6, 1), 3.92m, 3.83m)], after);
    }

    [Fact]
    public void Gross_rate_is_checked_when_known()
    {
        // A low net rate with a gross one at the minimum: the gross is the figure the convention sets.
        Assert.Empty(LegalMinimums.Check(3.00m, 4.20m, 4.00m, D(2026, 7, 1)));
        Assert.Equal(LegalMinimums.Codes.GrossRate, Assert.Single(LegalMinimums.Check(3.50m, 4.19m, 4.00m, D(2026, 7, 1))).Code);
    }

    [Fact]
    public void Breach_names_the_minimum_in_force_on_the_day()
    {
        var breach = Assert.Single(LegalMinimums.Check(2.80m, null, 4.00m, D(2025, 9, 1)));
        Assert.Equal((D(2025, 4, 1), 2.84m), (breach.Since, breach.Minimum));
    }

    [Fact]
    public void Before_the_tables_nothing_is_checked() => Assert.Empty(LegalMinimums.Check(1m, null, 4.00m, D(2025, 3, 31)));

    [Fact]
    public void Last_change_is_the_June_2026_revaluation() => Assert.Equal(D(2026, 6, 1), LegalMinimums.LastChange);

    [Fact]
    public void Minimums_only_rise()
    {
        // Check reads only the last day of a period: true only while no minimum goes down.
        Assert.True(LegalMinimums.Hourly.Zip(LegalMinimums.Hourly.Skip(1)).All(p => p.First.Gross < p.Second.Gross && p.First.Net < p.Second.Net));
        Assert.True(ChildcareRules.For(D(2026, 1, 1)).Upkeep9hMinimum < ChildcareRules.For(D(2026, 6, 1)).Upkeep9hMinimum);
    }

    [Fact]
    public void Upkeep_at_the_minimum_is_prorated_and_never_under_the_floor()
    {
        // 3,92 € for 9 h: 10 h → 4,36 €; 6 h → 2,61 € lifted to the 2,65 € floor; a contract under the minimum is paid the minimum prorated.
        Assert.Equal(4.36m, Upkeep.ForDay(D(2026, 6, 1), new LocalTime(8, 0), new LocalTime(18, 0), 3.92m));
        Assert.Equal(2.65m, Upkeep.ForDay(D(2026, 6, 1), new LocalTime(8, 0), new LocalTime(14, 0), 3.92m));
        Assert.Equal(4.36m, Upkeep.ForDay(D(2026, 6, 1), new LocalTime(8, 0), new LocalTime(18, 0), 3.80m));
    }
}
