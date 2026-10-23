using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Scenarios;

/// <summary>Property purchase scenario spec §3: the limits of a document and its balance. Fields are the camelCase wire paths.</summary>
public static class ScenarioRules
{
    public const int MaxLoans = 4;
    public const int MinLoanMonths = 12;
    public const int MaxLoanMonths = 420;

    /// <summary>The pre-filled annual rates per class, as fractions.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> Defaults = new Dictionary<string, decimal>
    {
        [AllocationClasses.Cash] = 0.02m,
        [AllocationClasses.MoneyMarket] = 0.025m,
        [AllocationClasses.Bond] = 0.03m,
        [AllocationClasses.Equity] = 0.06m,
        [AllocationClasses.Mixed] = 0.04m,
        [AllocationClasses.CapitalGuaranteed] = 0.025m,
        [AllocationClasses.RealEstate] = 0.02m,
        [AllocationClasses.Other] = 0m,
    };

    /// <summary>Every refusal of the document; empty when valid. A purchase date in the past is not refused (it is warned about).</summary>
    public static IReadOnlyList<(string Field, string Message)> Check(PropertyPurchase doc, LocalDate today)
    {
        var errors = new List<(string, string)>();
        void Refuse(string field, string message) => errors.Add((field, message));
        void NotNegative(string field, decimal value)
        {
            if (value < 0m) Refuse(field, "Must not be negative.");
        }

        if (doc.Price <= 0m) Refuse("price", "Must be greater than zero.");
        NotNegative("notaryFees", doc.NotaryFees);
        NotNegative("agencyFees", doc.AgencyFees);
        NotNegative("otherCosts", doc.OtherCosts);
        NotNegative("monthlyCharges", doc.MonthlyCharges);
        NotNegative("currentRent", doc.CurrentRent);
        NotNegative("expectedRent", doc.ExpectedRent);
        if (doc.VacancyMonths is < 0 or > 12) Refuse("vacancyMonths", "Must be from 0 to 12.");
        if (doc.RentalRegime is not (null or "micro" or "real")) Refuse("rentalRegime", "Must be micro, real or empty.");

        var seen = new HashSet<ItemId>();
        for (var i = 0; i < doc.Funding.Count; i++)
        {
            if (doc.Funding[i].Amount <= 0m) Refuse($"funding[{i}].amount", "Must be greater than zero.");
            if (!seen.Add(doc.Funding[i].Item)) Refuse($"funding[{i}].item", "An envelope can be used only once.");
        }

        if (doc.Loans.Count is < 1 or > MaxLoans) Refuse("loans", $"Between 1 and {MaxLoans} loans.");
        for (var i = 0; i < doc.Loans.Count; i++)
        {
            var loan = doc.Loans[i];
            var at = $"loans[{i}]";
            if (loan.Amount <= 0m) Refuse($"{at}.amount", "Must be greater than zero.");
            NotNegative($"{at}.rate", loan.Rate);
            if (loan.Months is < MinLoanMonths or > MaxLoanMonths) Refuse($"{at}.months", $"Must be from {MinLoanMonths} to {MaxLoanMonths}.");
            NotNegative($"{at}.fileFees", loan.FileFees);
            NotNegative($"{at}.guaranteeFees", loan.GuaranteeFees);
            NotNegative($"{at}.insurance.value", loan.Insurance.Value);
            if (loan.Deferral is { } deferral && (deferral.Months < 1 || deferral.Months >= loan.Months))
                Refuse($"{at}.deferral.months", "Must be at least 1 and shorter than the loan.");
        }

        var a = doc.Assumptions;
        if (a.HorizonYears is < 1 or > 30) Refuse("assumptions.horizonYears", "Must be from 1 to 30.");
        NotNegative("assumptions.monthlySavings", a.MonthlySavings);
        if (!AllocationClasses.All.Contains(a.SavingsClass)) Refuse("assumptions.savingsClass", "Unknown asset class.");
        foreach (var key in a.ClassRates.Keys.Where(k => !AllocationClasses.All.Contains(k)))
            Refuse($"assumptions.classRates.{key}", "Unknown asset class.");

        return errors;
    }

    /// <summary>(price + acquisition fees + loan fees) − (funding + loans); <c>null</c> when within 1 €.</summary>
    public static decimal? FundingGap(PropertyPurchase doc)
    {
        var needs = doc.Price + doc.NotaryFees + doc.AgencyFees + doc.OtherCosts + doc.Loans.Sum(l => l.FileFees + l.GuaranteeFees);
        var gap = needs - (doc.Funding.Sum(f => f.Amount) + doc.Loans.Sum(l => l.Amount));
        return Math.Abs(gap) <= 1m ? null : gap;
    }
}
