using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public class XirrTests
{
    [Fact]
    public void Ten_percent_over_a_leap_year()
        => Assert.Equal(10.0m, Xirr.AnnualPercent([(new LocalDate(2024, 1, 1), -1000m), (new LocalDate(2025, 1, 1), 1100m)], new LocalDate(2025, 1, 1)));

    [Fact]
    public void Null_under_twelve_months()
        => Assert.Null(Xirr.AnnualPercent([(new LocalDate(2024, 1, 1), -1000m), (new LocalDate(2024, 12, 31), 1100m)], new LocalDate(2024, 12, 31)));

    [Fact]
    public void A_full_year_with_no_flows_is_the_plain_change()
        => Assert.Equal(5.0m, Xirr.PeriodPercent([(new(2024, 12, 31), -1000m), (new(2025, 12, 31), 1050m)], new(2024, 12, 31), new(2025, 12, 31)));

    [Fact]
    public void A_quarter_is_not_annualised()
        => Assert.Equal(2.0m, Xirr.PeriodPercent([(new(2025, 12, 31), -1000m), (new(2026, 3, 31), 1020m)], new(2025, 12, 31), new(2026, 3, 31)));

    [Fact]
    public void One_sign_only_has_no_return()
        => Assert.Null(Xirr.PeriodPercent([(new(2025, 1, 1), -1000m), (new(2025, 6, 1), -500m)], new(2024, 12, 31), new(2025, 12, 31)));

    [Fact]
    public void A_contribution_midway_weighs_by_its_time()
        // 1000 at the start, 1000 more on 1 July, 2100 at the end: money-weighted, about 6.7 %, not 5 %.
        => Assert.InRange(Xirr.PeriodPercent([(new(2024, 12, 31), -1000m), (new(2025, 7, 1), -1000m), (new(2025, 12, 31), 2100m)], new(2024, 12, 31), new(2025, 12, 31))!.Value, 6.5m, 6.9m);
}
