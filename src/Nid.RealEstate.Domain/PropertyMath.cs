using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>Specs 3C1 §5–§6 and 3C2 §3–§4, README §5–§7: a property at d. Pure, exact decimals, in the property's currency.</summary>
public static class PropertyMath
{
    /// <summary>Spec 3C1 §2: a yearly estimate is stale after 400 days.</summary>
    public const int YearlyStaleDays = 400;

    /// <summary><c>stale_after_days</c> replaces the cadence's threshold; a manual cadence alone has none.</summary>
    public static int? StaleThreshold(PropertyCadence cadence, int? staleAfterDays)
        => staleAfterDays ?? (cadence == PropertyCadence.Yearly ? YearlyStaleDays : null);

    public static PropertyFigures At(
        PropertyDetails property, bool shared, IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows,
        IReadOnlyList<LinkedLoan> loans, IReadOnlyList<Tenancy> tenancies, LocalDate d)
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

        // Spec 3C2 §4: a rental with nothing saying since when it is let.
        if (property.Type == PropertyType.Rental && tenancies.Count == 0 && property.RentalStartsOn is null)
            warnings.Add(new(PropertyWarnings.RentalWithoutTenancy));

        var value = latest?.Value.Amount;
        return new PropertyFigures(value, latest?.AsOf, stale, costPrice, value - costPrice, balance, value - balance,
            realCost, lines, realCost - rents,
            loans.Select(l => new LinkedLoanBalance(l.Id, l.Figures.Balance, l.Rate is { } rate ? l.Figures.Balance * rate : null)).ToList(),
            warnings, Rental(property, flows, tenancies, costPrice, d));
    }

    /// <summary>Spec 3C2 §3: the amount over the months of its period (count, or count × 12 for years), rounded to the cent.</summary>
    public static decimal MonthlyEquivalent(RecurringCost cost)
    {
        var months = cost.Every.Unit switch
        {
            DurationUnit.Year => 12m * cost.Every.Count,
            DurationUnit.Month => cost.Every.Count,
            _ => throw new ArgumentOutOfRangeException(nameof(cost), "A recurring cost comes every month(s) or year(s)."),
        };
        return Math.Round(cost.Amount / months, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Spec 3C2 §3: the first <c>StartsOn + k × Every</c> (from the anchor each time) on or after d and before <c>EndsOn</c>; null once ended.</summary>
    public static LocalDate? NextDueOn(RecurringCost cost, LocalDate d)
    {
        for (var k = 0; ; k++)
        {
            var on = cost.Every.AddTo(cost.StartsOn, k);
            if (cost.EndsOn is { } end && on >= end)
                return null;
            if (on >= d)
                return on;
        }
    }

    /// <summary>Spec 3C2 §4: a tenancy covers its days from its start (inclusive) to its end (exclusive).</summary>
    public static bool Covers(Tenancy tenancy, LocalDate d) => tenancy.StartsOn <= d && (tenancy.EndsOn is not { } end || d < end);

    /// <summary>Spec 3E2 §2: the cost's occurrence in the calendar month of <paramref name="month"/> — a cost comes every month or more, so at most one.</summary>
    public static LocalDate? DueIn(RecurringCost cost, LocalDate month)
    {
        var first = new LocalDate(month.Year, month.Month, 1);
        return NextDueOn(cost, first) is { } on && on < first.PlusMonths(1) ? on : null;
    }

    /// <summary>
    /// Spec 3E2 §2: the rent a tenancy expects in the calendar month of <paramref name="month"/> — rent plus charges, prorated for a
    /// partial month exactly as the arrears' figure — and the first day of that month it covers; null when it covers none.
    /// </summary>
    public static (decimal Amount, LocalDate From)? RentIn(Tenancy tenancy, LocalDate month)
    {
        var first = new LocalDate(month.Year, month.Month, 1);
        var last = first.PlusMonths(1).PlusDays(-1);
        if (Covered(tenancy, first, last) == 0)
            return null;
        return (Expected(tenancy, first, last), tenancy.StartsOn > first ? tenancy.StartsOn : first);
    }

    /// <summary>Spec 3C2 §4 over W = (d − 12 months, d].</summary>
    private static RentalFigures Rental(PropertyDetails property, IReadOnlyList<Flow> flows, IReadOnlyList<Tenancy> tenancies, decimal costPrice, LocalDate d)
    {
        var first = d.PlusMonths(-12).PlusDays(1);
        var inWindow = flows.Where(f => f.OccurredOn >= first && f.OccurredOn <= d).ToList();
        var rent = inWindow.Where(f => f.Kind == FlowKind.Income && f.Category == PropertyCategories.Rent).Sum(f => f.Amount.Amount);
        // Spec 3C2 §1.4: works are in the cost price already; loan costs are never deducted.
        var charges = inWindow.Where(f => f.Kind == FlowKind.Expense && f.Category != PropertyCategories.Works).Sum(f => f.Amount.Amount);
        var expected = tenancies.Sum(t => Expected(t, first, d));

        var starts = tenancies.Select(t => t.StartsOn).ToList();
        if (property.RentalStartsOn is { } declared)
            starts.Add(declared);
        LocalDate? rentedSince = starts.Count == 0 ? null : starts.Min();

        int? vacantDays = null;
        decimal? vacancyRate = null;
        if (rentedSince is { } since && since <= d)
        {
            var from = since > first ? since : first;
            var days = Period.DaysBetween(from, d) + 1;
            vacantDays = days - tenancies.Sum(t => Covered(t, from, d));
            vacancyRate = (decimal)vacantDays.Value / days;
        }

        // Significant once let since d − 12 months or before (R before W's first day).
        var significant = rentedSince is { } r && r < first;
        return new RentalFigures(rentedSince, expected, rent, expected - rent, vacantDays, vacancyRate,
            significant ? rent / costPrice : null, significant ? (rent - charges) / costPrice : null);
    }

    /// <summary>Month by month over W: a month the tenancy covers whole counts in full, a partial one prorated to its days, half up to the cent.</summary>
    private static decimal Expected(Tenancy tenancy, LocalDate first, LocalDate d)
    {
        var monthly = tenancy.RentMonthly + tenancy.ChargesMonthly;
        var total = 0m;
        for (var month = new LocalDate(first.Year, first.Month, 1); month <= d; month = month.PlusMonths(1))
        {
            var next = month.PlusMonths(1);
            var length = Period.DaysBetween(month, next);
            var last = next.PlusDays(-1);
            var covered = Covered(tenancy, month > first ? month : first, last < d ? last : d);
            total += covered == length ? monthly : Math.Round(monthly * covered / length, 2, MidpointRounding.AwayFromZero);
        }
        return total;
    }

    /// <summary>The days of [from, to] the tenancy covers.</summary>
    private static int Covered(Tenancy tenancy, LocalDate from, LocalDate to)
    {
        var start = tenancy.StartsOn > from ? tenancy.StartsOn : from;
        var end = tenancy.EndsOn is { } ends && ends.PlusDays(-1) < to ? ends.PlusDays(-1) : to;
        return start > end ? 0 : Period.DaysBetween(start, end) + 1;
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
