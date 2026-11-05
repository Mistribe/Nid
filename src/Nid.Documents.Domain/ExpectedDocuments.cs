using Nid.Documents.Contracts;
using NodaTime;

namespace Nid.Documents.Domain;

/// <summary>
/// Issue #414: a yearly document for a period is expected from this day of the calendar year <paramref name="Lag"/> years after it
/// (a statement of year Y comes in Y + 1).
/// </summary>
public sealed record AnnualDue(int Month, int Day, int Lag = 0)
{
    public LocalDate In(int period) => new(period + Lag, Month, Day);

    /// <summary>
    /// The period a document covers, from its date, else its upload date: that year, or, for a lagged rule, the year <see cref="Lag"/>
    /// before when the day falls before the due day (the statement of Y, often dated early in Y + 1).
    /// </summary>
    public int PeriodOf(LocalDate? dated, LocalDate uploaded)
    {
        var on = dated ?? uploaded;
        return Lag > 0 && on < new LocalDate(on.Year, Month, Day) ? on.Year - Lag : on.Year;
    }
}

/// <summary>Issue #414: the role a subject's document plays, once (<paramref name="Due"/> null) or each calendar year.</summary>
public sealed record ExpectedRule(string Role, AnnualDue? Due);

/// <summary>
/// Issue #414: the documents a subject should hold, per kind of subject. A period is a calendar year, or <see cref="Once"/> for a
/// document expected once; a document covers the period <see cref="AnnualDue.PeriodOf"/> gives.
/// </summary>
public static class ExpectedDocumentRules
{
    public const int Once = 0;

    public const string LifeInsurance = "life_insurance";
    public const string TaxYear = "tax_year";
    public const string Loan = "loan";
    public const string Property = "property";

    public static readonly IReadOnlyDictionary<string, ExpectedRule> BySubject = new Dictionary<string, ExpectedRule>
    {
        // The insurer's statement of year Y (relevé de situation au 31/12, dated late in Y or early in Y + 1) comes in the first half of Y + 1.
        [LifeInsurance] = new(DocumentRoles.Statement, new AnnualDue(7, 1, Lag: 1)),
        // The notice (avis d'imposition) comes by the end of the summer.
        [TaxYear] = new(DocumentRoles.TaxNotice, new AnnualDue(9, 15)),
        [Loan] = new(DocumentRoles.AmortizationTable, null),
        [Property] = new(DocumentRoles.Deed, null),
    };

    /// <summary>
    /// The periods missing on <paramref name="today"/>, oldest first: neither <paramref name="covered"/> nor <paramref name="dismissed"/>.
    /// A yearly rule asks for each year whose due day is past, from the oldest year covered (as « Pas de bulletin » counts from the oldest
    /// payslip), or for the latest such year alone when none is (nothing while the oldest held is newer); never for a year due before
    /// <paramref name="since"/>, the subject's start.
    /// </summary>
    public static IReadOnlyList<int> Missing(ExpectedRule rule, LocalDate? since, LocalDate today, IReadOnlySet<int> covered, IReadOnlySet<int> dismissed)
    {
        if (rule.Due is not { } due)
            return covered.Contains(Once) || dismissed.Contains(Once) ? [] : [Once];

        var last = today.Year - due.Lag;
        if (today < due.In(last)) last--;
        var first = covered.Count > 0 ? covered.Min() : last;
        return Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Where(year => (since is null || since.Value.Year <= year && since < due.In(year)) && !covered.Contains(year) && !dismissed.Contains(year))
            .ToList();
    }
}
