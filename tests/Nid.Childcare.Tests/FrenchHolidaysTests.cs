using Nid.Childcare.Domain;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class FrenchHolidaysTests
{
    [Fact]
    public void Easter_based_days()
    {
        var y2026 = FrenchHolidays.In(2026);
        Assert.Contains(D(2026, 4, 6), y2026);
        Assert.Contains(D(2026, 5, 14), y2026);
        Assert.Contains(D(2026, 5, 25), y2026);
        Assert.Equal(11, y2026.Count);
        var y2027 = FrenchHolidays.In(2027);
        Assert.Contains(D(2027, 3, 29), y2027);
        Assert.Contains(D(2027, 5, 6), y2027);
        Assert.Contains(D(2027, 5, 17), y2027);
        Assert.Equal(11, y2027.Count);
    }
}
