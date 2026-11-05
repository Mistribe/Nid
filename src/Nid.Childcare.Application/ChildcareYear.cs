using System.Globalization;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Childcare.Application;

/// <summary>#411: a year of an arrangement's validated months, summed field by field.</summary>
public sealed record YearTotals(decimal NetPay, decimal Cmg, decimal Upkeep, decimal Meals, decimal TaxBase);

/// <summary>#411: an arrangement's year for the tax return, from the validated statements the tax estimate counts. Pure.</summary>
public static class ChildcareYear
{
    /// <summary>The 2042's boxes of childcare outside the home, one per child under 6 (« frais de garde des enfants de moins de 6 ans »).</summary>
    public static readonly IReadOnlyList<string> Boxes = ["7GA", "7GB", "7GC"];

    /// <summary>Decision 21: the validated statements of <paramref name="year"/> with their frozen figures, oldest first: what the tax estimate counts.</summary>
    public static IReadOnlyList<Statement> Counted(IEnumerable<Statement> statements, int year)
        => statements.Where(s => s.Validated && s.Month.Year == year && s.Figures is not null).OrderBy(s => s.Month).ToList();

    /// <summary>The validated months summed; <c>TaxBase</c> is the sum of the tax lines (net pay + upkeep − CMG), meals and mileage left out.</summary>
    public static YearTotals Totals(IReadOnlyList<Statement> counted)
        => new(counted.Sum(s => s.Figures!.Declaration.NetPay), counted.Sum(s => s.Figures!.Cmg), counted.Sum(s => s.Figures!.Declaration.Upkeep),
            counted.Sum(s => s.Figures!.Declaration.Meals), counted.Sum(s => s.Figures!.TaxBase));

    /// <summary>The years the arrangement has months in (decision 13), newest first.</summary>
    public static IReadOnlyList<int> Years(LocalDate start, LocalDate? end, LocalDate today)
        => StatementContext.Months(start, end, today).Select(m => m.Year).Distinct().ToList();

    /// <summary>The year just ended, the one the return is filed for; the latest the arrangement has when it has no month then.</summary>
    public static int DefaultYear(IReadOnlyList<int> years, LocalDate today)
        => years.Contains(today.Year - 1) || years.Count == 0 ? today.Year - 1 : years[0];

    /// <summary>The arrangement's months of <paramref name="year"/> without a validated statement, oldest first.</summary>
    public static IReadOnlyList<YearMonth> NotValidated(LocalDate start, LocalDate? end, LocalDate today, IEnumerable<Statement> statements, int year)
    {
        var validated = statements.Where(s => s.Validated).Select(s => s.Month).ToHashSet();
        return StatementContext.Months(start, end, today).Where(m => m.Year == year && !validated.Contains(m)).Reverse().ToList();
    }

    /// <summary>
    /// The child's box: its rank among the children with childcare costs that year, the eldest first (birth date, then name; unknown
    /// birth dates last). None past the third child.
    /// </summary>
    public static string? Box(PersonId child, IReadOnlyList<(PersonId Id, LocalDate? BirthDate, string Name)> children)
    {
        var ordered = children.DistinctBy(c => c.Id).OrderBy(c => c.BirthDate is null).ThenBy(c => c.BirthDate)
            .ThenBy(c => c.Name, StringComparer.Create(new CultureInfo("fr-FR"), true)).Select(c => c.Id).ToList();
        var rank = ordered.IndexOf(child);
        return rank >= 0 && rank < Boxes.Count ? Boxes[rank] : null;
    }
}
