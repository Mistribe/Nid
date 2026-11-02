using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;

namespace Nid.Childcare.Tests;

/// <summary>4,30 € net/h; Mon, Tue, Thu, Fri 08:00–17:30 with meal (38 h); 46 weeks; upkeep 3,80 € for 9 h; meal 3,20 €; overtime 10 %.</summary>
internal static class Fixture
{
    public static readonly LocalTime Arrival = new(8, 0), Departure = new(17, 30);

    public static readonly IReadOnlyList<WeekdaySlot> Schedule =
        [.. new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
            .Select(d => new WeekdaySlot(d, Arrival, Departure, true))];

    /// <summary>Six gap weeks, outside November and December 2026.</summary>
    public static readonly IReadOnlyList<CareGap> Gaps = [new(new LocalDate(2027, 7, 26), 4), new(new LocalDate(2027, 2, 15), 2)];

    public static readonly ContractTerms Terms = new(new LocalDate(2026, 9, 1), null, 4.30m, 0.10m, 3.80m, 3.20m);

    public const int ProgrammedWeeks = 46;

    public const decimal BasePay = 626.37m;

    public static LocalDate D(int y, int m, int d) => new(y, m, d);

    /// <summary>The fixture's one template week, from the Monday of the contract start.</summary>
    public static WeekPlan Plan => WeekPlan.For([new WeekType("Semaine type", ProgrammedWeeks, Schedule)], Terms.Start, []);

    private static IReadOnlyList<WeekdaySlot> Days(LocalTime tuesdayDeparture)
        => [.. new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
            .Select(d => new WeekdaySlot(d, new LocalTime(8, 15),
                d == IsoDayOfWeek.Tuesday ? tuesdayDeparture : new LocalTime(18, 0), true))];

    /// <summary>The household's two template weeks, Long then Short, alternating from the week of <paramref name="start"/>.</summary>
    public static WeekPlan Household(LocalDate start)
        => WeekPlan.For([new WeekType("Longue", 23, Days(new LocalTime(18, 0))), new WeekType("Courte", 22, Days(new LocalTime(16, 45)))], start, []);
}
