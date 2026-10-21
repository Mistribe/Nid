using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;
using Nid.Subscriptions.Application;

namespace Nid.Subscriptions.Tests;

public class ChargeCyclesTests
{
    [Theory]
    [InlineData(1, DurationUnit.Month, false)]
    [InlineData(2, DurationUnit.Month, true)]
    [InlineData(1, DurationUnit.Year, true)]
    [InlineData(5, DurationUnit.Week, true)]
    [InlineData(4, DurationUnit.Week, false)]
    [InlineData(32, DurationUnit.Day, true)]
    [InlineData(31, DurationUnit.Day, false)]
    public void Longer_than_a_month(int count, DurationUnit unit, bool expected)
        => Assert.Equal(expected, ChargeCycles.LongerThanAMonth(Duration.Create(count, unit).Ok()));
}
