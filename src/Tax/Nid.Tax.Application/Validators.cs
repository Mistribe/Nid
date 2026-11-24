using Nid.Platform.Contracts;
using Nid.Tax.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>
/// #515: the bounds of a profile's and a simulation's figures, checked in the pipeline whoever sends the request, each at its
/// field. <see cref="TaxChecks"/> states them; the handlers' whole-profile checks still answer the rest.
/// </summary>
internal static class TaxBounds
{
    public static void Rate(List<FieldError> errors, decimal? fraction, string path)
    {
        if (fraction is { } r && !TaxChecks.RateOk(r))
            errors.Add(new FieldError(path, "request.rate", "A contribution rate is a percent between 0 and 60."));
    }

    public static void Amount(List<FieldError> errors, decimal? amount, string path)
    {
        if (amount is { } a && !TaxChecks.AmountOk(a))
            errors.Add(new FieldError(path, "request.amount", "An amount is not negative and below 10 000 000."));
    }

    public static void SignedAmount(List<FieldError> errors, decimal amount, string path)
    {
        if (!TaxChecks.SignedAmountOk(amount))
            errors.Add(new FieldError(path, "request.amount", "A gain or a loss is below 10 000 000 either way."));
    }

    public static void Amounts(List<FieldError> errors, IReadOnlyDictionary<Guid, decimal>? amounts, string prefix)
    {
        foreach (var (person, amount) in amounts ?? new Dictionary<Guid, decimal>())
            Amount(errors, amount, $"{prefix}.{person}");
    }

    public static Result Answer(List<FieldError> errors)
        => errors.Count == 0 ? Result.Success() : Result.Failure(FieldErrors.Shape(errors));
}

[Validator]
internal sealed class SaveTaxProfileValidator : IRequestValidator<SaveTaxProfileCommand>
{
    public ValueTask<Result> ValidateAsync(SaveTaxProfileCommand request, CancellationToken cancellationToken = default)
    {
        var p = request.Profile;
        var errors = new List<FieldError>();
        foreach (var (person, rate) in p.ContributionRates ?? new Dictionary<Guid, decimal>())
            TaxBounds.Rate(errors, rate, $"contributionRates.{person}");
        TaxBounds.Amounts(errors, p.ActualExpenses, "actualExpenses");
        TaxBounds.Amounts(errors, p.PerUnusedCeilings, "perUnusedCeilings");
        TaxBounds.Amounts(errors, p.PerCeilings, "perCeilings");
        TaxBounds.Amounts(errors, p.PerCeilingsExpiring, "perCeilingsExpiring");
        TaxBounds.Amounts(errors, p.ExemptIncomes, "exemptIncomes");
        foreach (var (amount, path) in new[]
                 {
                     (p.RentalDeficitCarried, "rentalDeficitCarried"), (p.ChildcareOutsideHome, "childcareOutsideHome"), (p.HomeEmployment, "homeEmployment"),
                     (p.Donations, "donations"), (p.DonationsAid, "donationsAid"), (p.DonationsAidLate, "donationsAidLate"),
                     (p.RentalEnergyWorks, "rentalEnergyWorks"), (p.DeductibleCsg, "deductibleCsg"), (p.AlimonyPaid, "alimonyPaid"),
                     (p.AlimonyAdultChildren, "alimonyAdultChildren"), (p.CapitalLossesCarried, "capitalLossesCarried"),
                 })
            TaxBounds.Amount(errors, amount, path);
        return ValueTask.FromResult(TaxBounds.Answer(errors));
    }
}

[Validator]
internal sealed class SimulateTaxValidator : IRequestValidator<SimulateTaxQuery>
{
    public ValueTask<Result> ValidateAsync(SimulateTaxQuery request, CancellationToken cancellationToken = default)
    {
        var i = request.Inputs;
        var errors = new List<FieldError>();
        for (var n = 0; n < i.Declarants.Count; n++)
        {
            var d = i.Declarants[n];
            var p = $"declarants[{n}].";
            TaxBounds.Amount(errors, d.SalaryGross, p + "salaryGross");
            TaxBounds.Amount(errors, d.OtherTaxable, p + "otherTaxable");
            TaxBounds.Amount(errors, d.PerContributions, p + "perContributions");
            TaxBounds.Rate(errors, d.ContributionRate, p + "contributionRate");
            TaxBounds.Amount(errors, d.ActualExpenses, p + "actualExpenses");
            TaxBounds.Amount(errors, d.PerUnusedCeiling, p + "perUnusedCeiling");
            TaxBounds.Amount(errors, d.SalaryTaxable, p + "salaryTaxable");
            TaxBounds.Amount(errors, d.PerCeiling, p + "perCeiling");
            TaxBounds.Amount(errors, d.PreviousYearIncome, p + "previousYearIncome");
            TaxBounds.Amount(errors, d.PerExpiring, p + "perExpiring");
            TaxBounds.Amount(errors, d.ExemptIncome, p + "exemptIncome");
            TaxBounds.Amount(errors, d.Pensions, p + "pensions");
        }
        foreach (var (amount, path) in new[]
                 {
                     (i.RentalGross, "rentalGross"), (i.RentalInterest, "rentalInterest"), (i.RentalCharges, "rentalCharges"),
                     (i.CapitalIncome, "capitalIncome"), (i.NonTaxableIncome, "nonTaxableIncome"), (i.PinelReduction, "pinelReduction"),
                     (i.ChildcareOutsideHome, "childcareOutsideHome"), (i.HomeEmployment, "homeEmployment"), (i.Donations, "donations"),
                     (i.DonationsAid, "donationsAid"), (i.RentalDeficitCarried, "rentalDeficitCarried"), (i.DonationsAidLate, "donationsAidLate"),
                     (i.RentalEnergyWorks, "rentalEnergyWorks"), (i.CapitalDividends, "capitalDividends"), (i.DeductibleCsg, "deductibleCsg"),
                     (i.AlimonyPaid, "alimonyPaid"), (i.AlimonyAdultChildren, "alimonyAdultChildren"),
                 })
            TaxBounds.Amount(errors, amount, path);
        TaxBounds.SignedAmount(errors, i.RealisedGains, "realisedGains");
        TaxBounds.Amount(errors, i.CapitalLossesCarried, "capitalLossesCarried");
        return ValueTask.FromResult(TaxBounds.Answer(errors));
    }
}
