using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>Spec 3C1 §5–§6, README §7: a property at d. Pure, exact decimals, in the property's currency.</summary>
public static class PropertyMath
{
    /// <summary>Spec 3C1 §2: a yearly estimate is stale after 400 days.</summary>
    public const int YearlyStaleDays = 400;

    /// <summary><c>stale_after_days</c> replaces the cadence's threshold; a manual cadence alone has none.</summary>
    public static int? StaleThreshold(PropertyCadence cadence, int? staleAfterDays)
        => staleAfterDays ?? (cadence == PropertyCadence.Yearly ? YearlyStaleDays : null);

    public static PropertyFigures At(
        PropertyDetails property, bool shared, IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows,
        IReadOnlyList<LinkedLoan> loans, LocalDate d)
    {
        var latest = values.Where(v => v.AsOf <= d).OrderByDescending(v => v.AsOf).FirstOrDefault();
        var stale = latest is not null && StaleThreshold(property.Cadence, property.StaleAfterDays) is { } limit
                    && Period.DaysBetween(latest.AsOf, d) > limit;
        var past = flows.Where(f => f.OccurredOn <= d).ToList();
        var expenses = past.Where(f => f.Kind == FlowKind.Expense).ToList();

        var acquisition = property.PurchasePrice + property.NotaryFees + property.AgencyFees + property.OtherAcquisitionCosts;
        var costPrice = acquisition + expenses.Where(f => f.Category == PropertyCategories.Works).Sum(f => f.Amount.Amount);

        // A loan whose rate is missing is left out of the balance and of the cost (spec §5); fx_missing names it.
        var counted = loans.Where(l => l.Rate is not null).ToList();
        decimal Converted(Func<LoanFigures, decimal> part) => counted.Sum(l => part(l.Figures) * l.Rate!.Value);
        var balance = Converted(f => f.Balance);

        List<CostLine> lines =
        [
            new("acquisition", acquisition),
            new("loan_interest", Converted(f => f.InterestPaid)),
            new("loan_insurance", Converted(f => f.InsurancePaid)),
            new("loan_fees", Converted(f => f.FeesPaid)),
            .. expenses.GroupBy(f => f.Category ?? "other")
                .OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CostLine(g.Key, g.Sum(f => f.Amount.Amount))),
        ];
        var realCost = lines.Sum(l => l.Amount);
        var rents = past.Where(f => f.Kind == FlowKind.Income && f.Category == PropertyCategories.Rent).Sum(f => f.Amount.Amount);

        List<PropertyWarning> warnings = [];
        if (latest is null)
            warnings.Add(new(PropertyWarnings.NoValuation));
        else if (stale)
            warnings.Add(new(PropertyWarnings.ValuationStale));
        foreach (var loan in loans)
        {
            if (loan.Rate is null)
                warnings.Add(new(PropertyWarnings.FxMissing, loan.Id));
            else if (loan.OtherCurrency)
                warnings.Add(new(PropertyWarnings.LoanOtherCurrency, loan.Id));
            if (shared && loan.Private)
                warnings.Add(new(PropertyWarnings.VisibilityMismatch, loan.Id));
        }

        var value = latest?.Value.Amount;
        return new PropertyFigures(value, latest?.AsOf, stale, costPrice, value - costPrice, balance, value - balance,
            realCost, lines, realCost - rents,
            loans.Select(l => new LinkedLoanBalance(l.Id, l.Figures.Balance, l.Rate is { } rate ? l.Figures.Balance * rate : null)).ToList(),
            warnings);
    }

    /// <summary>The convention's order; a category no route writes goes last.</summary>
    private static int Rank(string category)
    {
        for (var i = 0; i < PropertyCategories.Expense.Count; i++)
            if (PropertyCategories.Expense[i] == category)
                return i;
        return PropertyCategories.Expense.Count;
    }
}
