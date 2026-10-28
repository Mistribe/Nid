using Nid.Core.Contracts;

namespace Nid.Core.Domain.Tax;

/// <summary>
/// Tax assessments spec §4: what a notice must satisfy before it is stored. One error per field, at its camelCase JSON path; the
/// code is always <see cref="TaxAssessmentCodes.InvalidAssessment"/> (<c>FieldError</c> lives above Core.Domain, the caller maps).
/// </summary>
public static class TaxAssessmentRules
{
    public const int FirstYear = 2000;

    /// <summary><paramref name="today"/> is the household's current year; <paramref name="adults"/> the household's adult person ids.</summary>
    public static IReadOnlyList<(string Path, string Message)> Check(TaxAssessment a, int today, IReadOnlySet<Guid> adults)
    {
        var errors = new List<(string Path, string Message)>();
        void Fail(string path, string message) => errors.Add((path, message));
        void Amount(string path, decimal? v, bool signed = false)
        {
            if (v is not { } x) return;
            if (!signed && x < 0) Fail(path, "Must not be negative.");
            else if (decimal.Round(x, 2) != x) Fail(path, "Two decimals at most.");
        }
        void Rate(string path, decimal? v)
        {
            if (v is { } x && (x is < 0 or > 100 || decimal.Round(x, 2) != x)) Fail(path, "Must be a percentage between 0 and 100.");
        }

        if (a.Year < FirstYear || a.Year > today - 1) Fail("year", $"Must be between {FirstYear} and {today - 1}.");
        if (a.IssuedYear < a.Year + 1) Fail("issuedYear", "Must be the year after the revenue year or later.");
        else if (a.IssuedYear > today) Fail("issuedYear", $"Must not be after {today}.");

        if (a.Declarants.Count is < 1 or > 2) Fail("declarants", "One or two declarants.");
        for (var i = 0; i < a.Declarants.Count; i++)
        {
            var d = a.Declarants[i];
            var p = $"declarants[{i}]";
            if (d.Rank is not (1 or 2) || a.Declarants.Take(i).Any(o => o.Rank == d.Rank)) Fail($"{p}.rank", "Rank 1 or 2, each once.");
            if (a.Declarants.Take(i).Any(o => o.Person == d.Person)) Fail($"{p}.personId", "A person is declarant once.");
            else if (!adults.Contains(d.Person.Value)) Fail($"{p}.personId", "Must be an adult of the household.");
            if (d.DeductionAmount is not null && d.Deduction is null) Fail($"{p}.deduction", "Needed with the deduction amount.");
            Amount($"{p}.grossSalaries", d.GrossSalaries);
            Amount($"{p}.deductionAmount", d.DeductionAmount);
            Amount($"{p}.netSalaries", d.NetSalaries);
            Amount($"{p}.perCeilingYear", d.PerCeilingYear);
            Amount($"{p}.perUnusedOldest", d.PerUnusedOldest);
            Amount($"{p}.perUnusedMiddle", d.PerUnusedMiddle);
            Amount($"{p}.perUnusedLatest", d.PerUnusedLatest);
            Amount($"{p}.perCeilingComputed", d.PerCeilingComputed);
            Amount($"{p}.perCeilingNext", d.PerCeilingNext);
        }

        Amount("shares", a.Shares);
        Amount("referenceIncome", a.ReferenceIncome);
        Rate("averageRate", a.AverageRate);
        Rate("marginalRate", a.MarginalRate);
        Amount("grossIncome", a.GrossIncome);
        Amount("taxableIncome", a.TaxableIncome);
        Amount("capitalIncomeDeclared", a.CapitalIncomeDeclared);
        Amount("capitalIncomeTaxable", a.CapitalIncomeTaxable);
        Amount("rentalIncomeNet", a.RentalIncomeNet);
        Amount("flatRateIncome", a.FlatRateIncome);
        Amount("exemptIncomeDeclared", a.ExemptIncomeDeclared);
        Amount("exemptIncomeNet", a.ExemptIncomeNet);
        Amount("scaleTax", a.ScaleTax);
        Amount("proportionalTax", a.ProportionalTax);
        Amount("taxBeforeCredits", a.TaxBeforeCredits);
        Amount("creditsDeclared", a.CreditsDeclared);
        Amount("creditsRetained", a.CreditsRetained);
        Amount("netTax", a.NetTax);
        Amount("withholding", a.Withholding);
        Amount("balance", a.Balance, signed: true);
        Amount("rentalDeficitCarried", a.RentalDeficitCarried);
        return errors;
    }
}
