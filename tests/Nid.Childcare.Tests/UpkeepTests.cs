using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class UpkeepTests
{
    [Fact]
    public void Day_of_9h30_uses_the_contract() => Assert.Equal(4.43m, Upkeep.ForDay(D(2026, 11, 2), Arrival, Departure, 4.20m));

    [Fact]
    public void Legal_minimum_beats_a_low_contract() => Assert.Equal(4.14m, Upkeep.ForDay(D(2026, 11, 2), Arrival, Departure, 3.50m));

    [Fact]
    public void Short_day_gets_the_floor() => Assert.Equal(2.65m, Upkeep.ForDay(D(2026, 11, 2), new LocalTime(8, 0), new LocalTime(11, 0), 4m));

    [Fact]
    public void Minimum_changes_on_1_June()
    {
        Assert.Equal(3.83m, Upkeep.ForDay(D(2026, 5, 29), new LocalTime(8, 0), new LocalTime(17, 0), 3m));
        Assert.Equal(3.92m, Upkeep.ForDay(D(2026, 6, 1), new LocalTime(8, 0), new LocalTime(17, 0), 3m));
    }

    [Fact]
    public void November_month()
        => Assert.Equal((70.38m, 54.40m), Upkeep.Month(StatementPreparation.Prepare(new YearMonth(2026, 11), Plan, Gaps), Terms));
}
