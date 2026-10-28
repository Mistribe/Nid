using Nid.Core.Contracts;

namespace Nid.Core.Domain.Tax;

/// <summary>Tax assessments spec §6: one compared figure; <c>Gap</c> is the notice minus Nid, to the cent.</summary>
public sealed record TaxGapLine(string Code, decimal Notice, decimal? Nid, decimal? Gap);

/// <summary>A declarant's net salaries on the notice against the estimate's.</summary>
public sealed record TaxDeclarantGap(Guid Person, decimal Notice, decimal Nid, decimal Gap);

public sealed record TaxGapsResult(IReadOnlyList<TaxGapLine> Lines, IReadOnlyList<TaxDeclarantGap> Declarants);

/// <summary>Tax assessments spec §6: a notice against the estimate of the same year, both sides computed by the server.</summary>
public static class TaxGaps
{
    /// <summary>
    /// The gaps of <paramref name="notice"/> with <paramref name="result"/>, whose declarants follow <paramref name="adults"/>; null
    /// when the notice's declarants are not exactly the adults. A figure the notice does not print is left out.
    /// </summary>
    public static TaxGapsResult? Of(TaxAssessment notice, TaxResult result, IReadOnlyList<Guid> adults)
    {
        if (!notice.Declarants.Select(d => d.Person.Value).ToHashSet().SetEquals(adults) || result.Declarants.Count != adults.Count)
            return null;

        var lines = new List<TaxGapLine>();
        void Line(string code, decimal? printed, decimal nid)
        {
            if (printed is { } p)
                lines.Add(new TaxGapLine(code, p, nid, Math.Round(p - nid, 2, MidpointRounding.AwayFromZero)));
        }
        Line("net_tax", notice.NetTax, result.IncomeTax + result.CapitalFlatTax);
        Line("taxable_income", notice.TaxableIncome, result.NetTaxableIncome);
        Line("reference_income", notice.ReferenceIncome, result.ReferenceIncome);
        Line("withholding", notice.Withholding, result.Withholding.Annual);
        Line("balance", notice.Balance, result.Withholding.Balance);

        var declarants = new List<TaxDeclarantGap>();
        for (var i = 0; i < adults.Count; i++)
            if (notice.Declarants.First(d => d.Person.Value == adults[i]).NetSalaries is { } printed)
            {
                var nid = result.Declarants[i].Taxable; // salaries and unemployment after the allowance, as the notice's line
                declarants.Add(new TaxDeclarantGap(adults[i], printed, nid, Math.Round(printed - nid, 2, MidpointRounding.AwayFromZero)));
            }
        return new TaxGapsResult(lines, declarants);
    }
}
