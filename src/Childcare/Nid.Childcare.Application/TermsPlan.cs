using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;

namespace Nid.Childcare.Application;

/// <summary>A revision's week types as the plan the resolver reads.</summary>
public static class TermsPlan
{
    public static WeekPlan Of(Terms t, LocalDate contractStart, IReadOnlyList<WeekOverride> overrides)
        // An exception was checked against the revision of its own week: for another revision a rank it lacks falls back to the rotation.
        => WeekPlan.For(t.WeekTypes, contractStart, [.. overrides.Where(o => o.WeekType < t.WeekTypes.Count)]);
}
