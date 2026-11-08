using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;

namespace Nid.Core.Application.Tax;

/// <summary>
/// Tax assessments spec §2: one declarant as the API shows it; <c>Deduction</c> is <c>allowance</c> or <c>actual</c>. Amounts are euros,
/// absent on the notice means null (never 0).
/// </summary>
public sealed record TaxAssessmentDeclarantView(
    Guid PersonId, int Rank, DashboardMoneyView? GrossSalaries, string? Deduction, DashboardMoneyView? DeductionAmount, DashboardMoneyView? NetSalaries,
    DashboardMoneyView? PerCeilingYear, DashboardMoneyView? PerUnusedOldest, DashboardMoneyView? PerUnusedMiddle, DashboardMoneyView? PerUnusedLatest,
    DashboardMoneyView? PerCeilingComputed, DashboardMoneyView? PerCeilingNext, DashboardMoneyView? OvertimeExempt, DashboardMoneyView? Withholding,
    DashboardMoneyView? CreditsAdvance);

/// <summary>
/// Tax assessments spec §2: a stored notice, its <c>Version</c> to send back with a correction. Rates are percent strings.
/// <c>EffectiveAverageRate</c>: the notice's average rate, else its net tax over its taxable income, as the estimate computes it;
/// null without either figure. Read-only: a correction sends back <c>AverageRate</c>.
/// </summary>
public sealed record TaxAssessmentView(
    Guid Id, uint Version, int Year, int IssuedYear, IReadOnlyList<TaxAssessmentDeclarantView> Declarants, string? Shares, DashboardMoneyView? ReferenceIncome,
    string? AverageRate, string? MarginalRate, DashboardMoneyView? GrossIncome, DashboardMoneyView? TaxableIncome, DashboardMoneyView? CapitalIncomeDeclared,
    DashboardMoneyView? CapitalIncomeTaxable, DashboardMoneyView? RentalIncomeNet, DashboardMoneyView? FlatRateIncome, DashboardMoneyView? ExemptIncomeDeclared,
    DashboardMoneyView? ExemptIncomeNet, DashboardMoneyView? ScaleTax, DashboardMoneyView? ProportionalTax, DashboardMoneyView? TaxBeforeCredits,
    DashboardMoneyView? CreditsDeclared, DashboardMoneyView? CreditsRetained, DashboardMoneyView? NetTax, DashboardMoneyView? Withholding,
    DashboardMoneyView? Balance, DashboardMoneyView? RentalDeficitCarried, Guid? DocumentId, DashboardMoneyView? DividendsEligible,
    DashboardMoneyView? CapitalIncomeAlreadyLevied, DashboardMoneyView? FlatRatePrepaidCredit, DashboardMoneyView? RentalDeficitOnRentalIncome,
    DashboardMoneyView? RentalDeficitOnGlobalIncome, DashboardMoneyView? PinelReduction, DashboardMoneyView? ChildcareExpenses,
    string? EffectiveAverageRate) : IExposesDocuments
{
    public IEnumerable<Guid> ExposedDocuments() => DocumentId is { } d ? [d] : [];

    public object WithReadableDocuments(IReadOnlySet<Guid> readable) => this with { DocumentId = readable.Keep(DocumentId) };

    public static TaxAssessmentView Of(StoredTaxAssessment s)
    {
        var a = s.Assessment;
        return new TaxAssessmentView(
            s.Id, s.Version, a.Year, a.IssuedYear, a.Declarants.OrderBy(d => d.Rank).Select(Of).ToList(), Plain(a.Shares), Euros(a.ReferenceIncome),
            Plain(a.AverageRate), Plain(a.MarginalRate), Euros(a.GrossIncome), Euros(a.TaxableIncome), Euros(a.CapitalIncomeDeclared),
            Euros(a.CapitalIncomeTaxable), Euros(a.RentalIncomeNet), Euros(a.FlatRateIncome), Euros(a.ExemptIncomeDeclared), Euros(a.ExemptIncomeNet),
            Euros(a.ScaleTax), Euros(a.ProportionalTax), Euros(a.TaxBeforeCredits), Euros(a.CreditsDeclared), Euros(a.CreditsRetained), Euros(a.NetTax),
            Euros(a.Withholding), Euros(a.Balance), Euros(a.RentalDeficitCarried), a.DocumentId, Euros(a.DividendsEligible),
            Euros(a.CapitalIncomeAlreadyLevied), Euros(a.FlatRatePrepaidCredit), Euros(a.RentalDeficitOnRentalIncome),
            Euros(a.RentalDeficitOnGlobalIncome), Euros(a.PinelReduction), Euros(a.ChildcareExpenses), Plain(AverageRateOf(a)));
    }

    private static decimal? AverageRateOf(TaxAssessment a)
        => a.AverageRate ?? (a is { NetTax: { } tax, TaxableIncome: > 0m and var taxable }
            ? Math.Round(Math.Max(0m, tax) / taxable * 100m, 2, MidpointRounding.AwayFromZero)
            : null);

    private static TaxAssessmentDeclarantView Of(TaxAssessmentDeclarant d)
        => new(d.Person.Value, d.Rank, Euros(d.GrossSalaries), d.Deduction?.ToString().ToLowerInvariant(), Euros(d.DeductionAmount), Euros(d.NetSalaries),
            Euros(d.PerCeilingYear), Euros(d.PerUnusedOldest), Euros(d.PerUnusedMiddle), Euros(d.PerUnusedLatest), Euros(d.PerCeilingComputed),
            Euros(d.PerCeilingNext), Euros(d.OvertimeExempt), Euros(d.Withholding), Euros(d.CreditsAdvance));

    private static DashboardMoneyView? Euros(decimal? v) => v is { } x ? new DashboardMoneyView(TaxEstimates.Amount(x), "EUR") : null;

    /// <summary>A share count or a rate as the notice prints it: two decimals at most, none when whole.</summary>
    private static string? Plain(decimal? v) => v?.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Tax assessments spec §6: the reader's notices, newest year first, then by the first declarant's name.</summary>
public sealed record TaxAssessmentList(IReadOnlyList<TaxAssessmentView> Items) : IExposesDocuments
{
    public IEnumerable<Guid> ExposedDocuments() => Items.SelectMany(i => i.ExposedDocuments());

    public object WithReadableDocuments(IReadOnlySet<Guid> readable) => new TaxAssessmentList(Items.Select(i => (TaxAssessmentView)i.WithReadableDocuments(readable)).ToList());
}

/// <summary>
/// The notice's « Déduction 10 % » line for declared salaries, and the net salaries left after it, as the form prefills them.
/// Whole euros, as the notice prints them; the person may still correct either figure.
/// </summary>
public sealed record SalaryAllowanceView(DashboardMoneyView DeductionAmount, DashboardMoneyView NetSalaries)
{
    public static SalaryAllowanceView Of(TaxRules rules, decimal grossSalaries)
    {
        var deduction = Math.Round(rules.SalaryAllowance(grossSalaries), 0, MidpointRounding.AwayFromZero);
        return new SalaryAllowanceView(Euros(deduction), Euros(grossSalaries - deduction));
    }

    private static DashboardMoneyView Euros(decimal v) => new(TaxEstimates.Amount(v), "EUR");
}
