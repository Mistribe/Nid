using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §0 and decision 14: <c>ChildcareMonth</c>'s day rules as (field path, code) problems, for days whose
/// <c>Planned</c> is already recomputed from the template. Empty when the month can be declared. Pure.
/// </summary>
public static class StatementCheck
{
    public static IReadOnlyList<(string Field, string Code)> Days(YearMonth month, IReadOnlyList<StatementDay> days)
    {
        var problems = new List<(string, string)>();
        var count = month.Calendar.GetDaysInMonth(month.Year, month.Month);
        var dates = days.Select(d => d.Date).ToHashSet();
        if (days.Count != count || dates.Count != count || dates.Any(d => d.ToYearMonth() != month))
            problems.Add(("days", ContractCheck.Codes.DaysIncomplete));
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            if (day.Present && (day.Arrival is not { } a || day.Departure is not { } d || d <= a))
                problems.Add(($"days[{i}].departure", ContractCheck.Codes.DepartureBeforeArrival));
            if (day.Planned && !day.Present && day.Absence is null)
                problems.Add(($"days[{i}].absence", ContractCheck.Codes.AbsenceRequired));
            if (day.Absence is not null && !day.Planned)
                problems.Add(($"days[{i}].absence", ContractCheck.Codes.AbsenceNotAllowed));
        }
        return problems;
    }
}
