using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>Retirement spec §1: when a person leaves work, and the projection month their work income stops being counted.</summary>
public static class Departures
{
    /// <summary>The month of the departure: the birth date plus the departure age in years and months. Null without a birth date.</summary>
    public static YearMonth? MonthOf(PersonInfo person)
        => person.BirthDate?.PlusYears(person.RetirementAge).PlusMonths(person.RetirementMonths).ToYearMonth();

    /// <summary>
    /// Per adult with a birth date, the first projection month after the departure (k + 1, k being the months from today's month
    /// to the departure month). Not above one when the departure is past; no upper bound.
    /// </summary>
    public static IReadOnlyDictionary<PersonId, int> SwitchMonths(IEnumerable<PersonInfo> persons, LocalDate today)
    {
        var now = today.ToYearMonth();
        var result = new Dictionary<PersonId, int>();
        foreach (var p in persons)
            if (p.Kind == PersonKind.Adult && MonthOf(p) is { } month)
                result[p.Id] = (month.Year - now.Year) * 12 + month.Month - now.Month + 1;
        return result;
    }

    /// <summary>The latest switch among the owners; null when there are none or one of them has no switch (the income then never stops).</summary>
    public static int? SwitchOf(IEnumerable<PersonId> owners, IReadOnlyDictionary<PersonId, int> switches)
    {
        int? latest = null;
        foreach (var o in owners)
        {
            if (!switches.TryGetValue(o, out var s))
                return null;
            latest = Math.Max(latest ?? s, s);
        }
        return latest;
    }
}
