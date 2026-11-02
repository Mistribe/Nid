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
        Assert.Equal(146, Mensualisation.NormalHours(1748m));
    }

    [Fact]
    public void Weighted_figures_of_the_household()
    {
        var types = Household(D(2026, 9, 1)).Types;
        Assert.Equal(1727.5m, Mensualisation.AnnualHours(types));
        Assert.Equal(698.20m, Mensualisation.BasePay(4.85m, 1727.5m));
        Assert.Equal(144, Mensualisation.NormalHours(1727.5m));
        Assert.Equal(15, Mensualisation.ActivityDays(types));
    }

    [Fact]
    public void One_week_type_gives_the_fixture_base_pay()
        => Assert.Equal(BasePay, Mensualisation.BasePay(4.30m, Mensualisation.AnnualHours(Plan.Types)));

    [Fact]
    public void Activity_days_round_up() // 4 days x 46 weeks / 12 = 15.33
        => Assert.Equal(16, Mensualisation.ActivityDays([new WeekType("Semaine type", ProgrammedWeeks, Schedule)]));
}
