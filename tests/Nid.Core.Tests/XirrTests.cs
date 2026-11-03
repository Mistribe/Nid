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
}
