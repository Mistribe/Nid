using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>
/// Net worth projection spec §2.1: the household's assumptions. Rates and inflation are annual fractions (<c>0.06</c> is 6 %);
/// <c>MonthlySavings</c> is in the reference currency, <c>null</c> meaning automatic. Inflation spec C15: <c>Inflation</c> and
/// <c>ClassRates</c> are the long-term rates; <c>InflationByYear</c> and <c>ClassRatesByYear</c> (cash and money market only) set a
/// calendar year's rate; incomes grow by inflation plus <c>IncomeGrowth</c> (D6). Ranges spec §2: <c>ClassVolatility</c> is each class's
/// annual volatility (a fraction), a class absent (or the whole table null) taking <see cref="ProjectionDefaults.ClassVolatility"/>.
/// </summary>
public sealed record ProjectionAssumptions(
    IReadOnlyDictionary<string, decimal> ClassRates, decimal Inflation, decimal? MonthlySavings, string SavingsClass, bool FreedPaymentsToSavings,
    IReadOnlyDictionary<int, decimal>? InflationByYear = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, decimal>>? ClassRatesByYear = null,
    decimal IncomeGrowth = 0m,
    IReadOnlyDictionary<string, decimal>? ClassVolatility = null)
{
    /// <summary>The class's annual volatility: its own, else the default (0 for a class with none).</summary>
    public decimal VolatilityOf(string @class)
        => ClassVolatility is { } table && table.TryGetValue(@class, out var own) ? own : ProjectionDefaults.ClassVolatility.GetValueOrDefault(@class);
}

/// <summary>What a household that never chose assumptions gets; the property purchase scenario starts from the same rates.</summary>
public static class ProjectionDefaults
{
    /// <summary>The pre-filled annual rates per class, as fractions.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> ClassRates = new Dictionary<string, decimal>
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

    /// <summary>
    /// Ranges spec §2: the pre-filled annual volatility per class, as fractions. Cash, money market and capital-guaranteed funds do not
    /// fall; « other » is unknown, so left flat.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, decimal> ClassVolatility = new Dictionary<string, decimal>
    {
        [AllocationClasses.Cash] = 0m,
        [AllocationClasses.MoneyMarket] = 0m,
        [AllocationClasses.Bond] = 0.06m,
        [AllocationClasses.Equity] = 0.16m,
        [AllocationClasses.Mixed] = 0.1m,
        [AllocationClasses.CapitalGuaranteed] = 0m,
        [AllocationClasses.RealEstate] = 0.1m,
        [AllocationClasses.Other] = 0m,
    };

    public static readonly ProjectionAssumptions Assumptions = new(ClassRates, 0.02m, null, AllocationClasses.Cash, true);
}

/// <summary>Net worth projection spec §4: the kinds of event a projection scenario holds.</summary>
public static class ProjectionEventKinds
{
    public const string IncomeChange = "income_change";
    public const string SavingsChange = "savings_change";
    public const string EarlyRepayment = "early_repayment";
    public const string PropertySale = "property_sale";
    public const string PropertyPurchase = "property_purchase";
}

/// <summary>Spec §4: how an early repayment is spread over the loans of a property.</summary>
public static class ProjectionSplits
{
    public const string HighestRate = "highest_rate";
    public const string SmallestBalance = "smallest_balance";
    public const string Chosen = "chosen";

    public static readonly IReadOnlyList<string> All = [HighestRate, SmallestBalance, Chosen];
}

/// <summary>Spec §4: what an early repayment shortens.</summary>
public static class ProjectionEffects
{
    public const string ReduceTerm = "reduce_term";
    public const string ReducePayment = "reduce_payment";

    public static readonly IReadOnlyList<string> All = [ReduceTerm, ReducePayment];
}

/// <summary>Spec §4: a date, or the date a person reaches an age. Exactly one of <c>On</c>, or <c>Person</c> with <c>Age</c>.</summary>
public sealed record EventDate(LocalDate? On, PersonId? Person, int? Age);

/// <summary>
/// Spec §4: one event of a scenario; which fields apply depends on <c>Kind</c> (income and savings changes use <c>MonthlyAmount</c>
/// and <c>Until</c>, an early repayment <c>Amount</c>, <c>Split</c>, <c>Loan</c>, <c>Effect</c> and <c>Penalty</c>, a sale
/// <c>Property</c>, <c>Price</c> and <c>Costs</c>, a purchase <c>Scenario</c>).
/// </summary>
public sealed record ProjectionEvent(
    Guid Id, string Kind, EventDate Start, EventDate? Until, string? Label, decimal? MonthlyAmount,
    decimal? Amount, string? Split, ItemId? Loan, string? Effect, decimal? Penalty,
    ItemId? Property, decimal? Price, decimal? Costs, ItemId? Scenario);

/// <summary>Spec §6: what a scenario changes of the household's assumptions; a null field keeps the household's.</summary>
public sealed record ProjectionOverrides(
    IReadOnlyDictionary<string, decimal>? ClassRates, decimal? Inflation, decimal? MonthlySavings, string? SavingsClass, bool? FreedPaymentsToSavings,
    IReadOnlyDictionary<int, decimal>? InflationByYear = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, decimal>>? ClassRatesByYear = null,
    decimal? IncomeGrowth = null,
    IReadOnlyDictionary<string, decimal>? ClassVolatility = null)
{
    /// <summary>
    /// Class rates and volatilities merged key by key, the yearly class tables class by class; the inflation table replaced whole (C18); every other
    /// field replaced when set.
    /// </summary>
    public ProjectionAssumptions Apply(ProjectionAssumptions household)
    {
        var rates = household.ClassRates.ToDictionary(r => r.Key, r => r.Value);
        foreach (var (@class, rate) in ClassRates ?? new Dictionary<string, decimal>())
            rates[@class] = rate;
        var yearly = household.ClassRatesByYear;
        if (ClassRatesByYear is not null)
        {
            var merged = (household.ClassRatesByYear ?? new Dictionary<string, IReadOnlyDictionary<int, decimal>>()).ToDictionary(c => c.Key, c => c.Value);
            // A null table (`{"cash": null}` on the wire) is absent: the household's stays.
            foreach (var (@class, table) in ClassRatesByYear)
                if (table is not null) merged[@class] = table;
            yearly = merged;
        }
        var volatility = household.ClassVolatility;
        if (ClassVolatility is not null)
        {
            var merged = (household.ClassVolatility ?? new Dictionary<string, decimal>()).ToDictionary(v => v.Key, v => v.Value);
            foreach (var (@class, sigma) in ClassVolatility) merged[@class] = sigma;
            volatility = merged;
        }
        return new ProjectionAssumptions(
            rates, Inflation ?? household.Inflation, MonthlySavings ?? household.MonthlySavings,
            SavingsClass ?? household.SavingsClass, FreedPaymentsToSavings ?? household.FreedPaymentsToSavings,
            InflationByYear ?? household.InflationByYear, yearly, IncomeGrowth ?? household.IncomeGrowth, volatility);
    }
}

/// <summary>Spec §6: the stored form of the horizon, <c>Years</c> or until <c>RetireOf</c> retires.</summary>
public sealed record ScenarioHorizon(int? Years, PersonId? RetireOf);

/// <summary>Spec §6: the document of a projection scenario.</summary>
public sealed record ProjectionScenario(int Version, ScenarioHorizon Horizon, ProjectionOverrides Assumptions, IReadOnlyList<ProjectionEvent> Events);
