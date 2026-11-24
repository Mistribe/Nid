using Nid.Core.Contracts;
using Nid.Tax.Contracts;

namespace Nid.Tax.Domain;

/// <summary>
/// Tax-per spec §3.4: the contributions a person's payslips show, as a fraction of the gross: what the gross loses to reach the net
/// taxable income, the non-deductible CSG added back (as the calculator adds it), over the gross. Only the payments holding both
/// figures count (<see cref="TaxLineCodes.SalaryRateGross"/>, <see cref="TaxLineCodes.SalaryRateTaxable"/>); null when there is none.
/// </summary>
public static class ObservedRate
{
    public static decimal? Of(IReadOnlyList<TaxLine> lines, PersonId adult)
    {
        decimal Sum(string code) => lines.Where(l => l.Code == code && l.Person == adult).Sum(l => l.Amount);
        var gross = Sum(TaxLineCodes.SalaryRateGross);
        return gross > 0m ? Math.Round((gross - Sum(TaxLineCodes.SalaryRateTaxable) + TaxRules.NonDeductibleCsg * gross) / gross, 4, MidpointRounding.AwayFromZero) : null;
    }
}
