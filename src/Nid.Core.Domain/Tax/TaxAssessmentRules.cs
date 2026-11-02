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
            if (d is { GrossSalaries: { } gross, DeductionAmount: { } deducted } && deducted > gross) Fail($"{p}.deductionAmount", "Must not exceed the salaries.");
            Amount($"{p}.grossSalaries", d.GrossSalaries);
            Amount($"{p}.deductionAmount", d.DeductionAmount);
            Amount($"{p}.netSalaries", d.NetSalaries);
            Amount($"{p}.perCeilingYear", d.PerCeilingYear);
            Amount($"{p}.perUnusedOldest", d.PerUnusedOldest);
            Amount($"{p}.perUnusedMiddle", d.PerUnusedMiddle);
            Amount($"{p}.perUnusedLatest", d.PerUnusedLatest);
            Amount($"{p}.perCeilingComputed", d.PerCeilingComputed);
            Amount($"{p}.perCeilingNext", d.PerCeilingNext);
            Amount($"{p}.overtimeExempt", d.OvertimeExempt);
            Amount($"{p}.withholding", d.Withholding);
            Amount($"{p}.creditsAdvance", d.CreditsAdvance);
        }

        Amount("shares", a.Shares);
        Amount("referenceIncome", a.ReferenceIncome);
        Rate("averageRate", a.AverageRate);
        Rate("marginalRate", a.MarginalRate);
        Amount("grossIncome", a.GrossIncome);
        Amount("taxableIncome", a.TaxableIncome);
        Amount("capitalIncomeDeclared", a.CapitalIncomeDeclared);
        Amount("capitalIncomeTaxable", a.CapitalIncomeTaxable);
        Amount("rentalIncomeNet", a.RentalIncomeNet, signed: true);
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
        Amount("dividendsEligible", a.DividendsEligible);
        Amount("capitalIncomeAlreadyLevied", a.CapitalIncomeAlreadyLevied);
        Amount("flatRatePrepaidCredit", a.FlatRatePrepaidCredit);
        Amount("rentalDeficitOnRentalIncome", a.RentalDeficitOnRentalIncome);
        Amount("rentalDeficitOnGlobalIncome", a.RentalDeficitOnGlobalIncome);
        Amount("pinelReduction", a.PinelReduction);
        Amount("childcareExpenses", a.ChildcareExpenses);
        return errors;
    }

    /// <summary>
    /// Tax assessments spec §4: the lines the notice only adds up, worked out from the ones it adds; a line whose sources are not typed
    /// keeps the value sent. A declarant's net salaries are the salaries less the deduction; the ceiling for next year's contributions
    /// is the three unused ceilings plus the one computed on the revenues (an unused one left blank counts 0); the year's total ceiling
    /// is the one the <paramref name="previousYear"/> notice where the same person declares gave for this year.
    /// </summary>
    public static TaxAssessment Complete(TaxAssessment a, IEnumerable<TaxAssessment> previousYear)
    {
        var earlier = previousYear.Where(n => n.Year == a.Year - 1).SelectMany(n => n.Declarants).ToList();
        return a with
        {
            Declarants = a.Declarants.Select(d => d with
            {
                NetSalaries = d is { GrossSalaries: { } gross, DeductionAmount: { } deducted } ? gross - deducted : d.NetSalaries,
                PerCeilingNext = d.PerCeilingComputed is { } computed
                    ? (d.PerUnusedOldest ?? 0m) + (d.PerUnusedMiddle ?? 0m) + (d.PerUnusedLatest ?? 0m) + computed
                    : d.PerCeilingNext,
                PerCeilingYear = earlier.FirstOrDefault(e => e.Person == d.Person && e.PerCeilingNext is not null)?.PerCeilingNext ?? d.PerCeilingYear,
            }).ToList(),
        };
    }
}
