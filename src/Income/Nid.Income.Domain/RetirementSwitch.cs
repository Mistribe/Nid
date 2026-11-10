using Nid.Income.Contracts;
using NodaTime;

namespace Nid.Income.Domain;

/// <summary>Retirement spec §2: which income stops at an adult's departure, and where a not-yet-started pension moves to.</summary>
public static class RetirementSwitch
{
    public static bool Stops(IncomeKind kind) => kind is IncomeKind.Salary or IncomeKind.SelfEmployment or IncomeKind.Unemployment;

    /// <summary>
    /// A pension whose earliest phase starts after <paramref name="today"/> starts on <paramref name="startOn"/> instead: every phase
    /// shifts by the same number of days. A pension already running keeps its dates.
    /// </summary>
    public static IReadOnlyList<IncomePhase> MovePension(IReadOnlyList<IncomePhase> phases, LocalDate today, LocalDate startOn)
    {
        if (phases.Count == 0)
            return phases;
        var first = phases.Min(p => p.StartsOn);
        if (first <= today)
            return phases;
        var days = Period.Between(first, startOn, PeriodUnits.Days).Days;
        return phases.Select(p => p with { StartsOn = p.StartsOn.PlusDays(days), EndsOn = p.EndsOn?.PlusDays(days) }).ToList();
    }
}
