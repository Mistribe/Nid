using Nid.Childcare.Domain;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class PaidLeaveTests
{
    [Fact]
    public void Reference_period_starts_the_june_before() => Assert.Equal(D(2026, 6, 1), PaidLeave.ReferenceStart(D(2027, 6, 1)));

    [Fact]
    public void Full_reference_year()
    {
        Assert.Equal(30, PaidLeave.Accrued(12));
        Assert.Equal(30, PaidLeave.Accrued(14));
    }

    [Fact]
    public void Partial_year_rounds_up() => Assert.Equal(18, PaidLeave.Accrued(7));

    [Fact]
    public void Maintained_pay_wins() => Assert.Equal(817.00m, PaidLeave.JuneIndemnity(6500m, 4.30m, 38m, 30));

    [Fact]
    public void Tenth_wins() => Assert.Equal(840.00m, PaidLeave.JuneIndemnity(8400m, 4.30m, 38m, 30));

    [Fact]
    public void Pajemploi_example() => Assert.Equal(600.00m, PaidLeave.JuneIndemnity(5400m, 5m, 40m, 18));

    [Fact]
    public void Average_week_of_an_uneven_year() => Assert.Equal(825.36m, PaidLeave.JuneIndemnity(6500m, 4.30m, 1727.5m / 45m, 30));
}
