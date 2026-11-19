using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class MensualisationTests
{
    [Fact]
    public void Weekly_hours_from_template() => Assert.Equal(38m, Mensualisation.WeeklyHours(Schedule));

    [Fact]
    public void Base_pay_of_the_fixture()
    {
        Assert.Equal(626.37m, Mensualisation.BasePay(4.30m, 1748m));
        Assert.Equal(145, Mensualisation.NormalHours(1748m)); // 145.67
    }

    [Fact]
    public void Weighted_figures_of_the_household()
    {
        var types = Household(D(2026, 9, 1)).Types;
        Assert.Equal(1727.5m, Mensualisation.AnnualHours(types));
        Assert.Equal(698.20m, Mensualisation.BasePay(4.85m, 1727.5m));
        Assert.Equal(143, Mensualisation.NormalHours(1727.5m)); // 143.96
        Assert.Equal(15, Mensualisation.ActivityDays(types));
    }

    /// <summary>#225: the payroll tools cut the normal hours to the whole hour, never up, whatever the pay's cents.</summary>
    [Theory]
    [InlineData(566.00, 4.00, 141)]  // 141.5 h: a month with one hour deducted
    [InlineData(1054.04, 4.00, 263)] // 263.51 h: a month with paid leave
    [InlineData(559.90, 3.9993, 140)] // 139.9995 h: 140 h whose pay was rounded to the cent
    public void Normal_hours_are_cut_to_the_hour(decimal payDue, decimal rate, int hours)
        => Assert.Equal(hours, Mensualisation.NormalHours(payDue, rate));

    [Fact]
    public void One_week_type_gives_the_fixture_base_pay()
        => Assert.Equal(BasePay, Mensualisation.BasePay(4.30m, Mensualisation.AnnualHours(Plan.Types)));

    [Fact]
    public void Activity_days_round_up() // 4 days x 46 weeks / 12 = 15.33
        => Assert.Equal(16, Mensualisation.ActivityDays([new WeekType("Semaine type", ProgrammedWeeks, Schedule)]));
}
