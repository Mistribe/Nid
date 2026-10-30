using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>
/// Net worth projection spec §2.1: the household's assumptions. Rates and inflation are annual fractions (<c>0.06</c> is 6 %);
/// <c>MonthlySavings</c> is in the reference currency, <c>null</c> meaning automatic.
/// </summary>
public sealed record ProjectionAssumptions(
    IReadOnlyDictionary<string, decimal> ClassRates, decimal Inflation, decimal? MonthlySavings, string SavingsClass, bool FreedPaymentsToSavings);

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
    IReadOnlyDictionary<string, decimal>? ClassRates, decimal? Inflation, decimal? MonthlySavings, string? SavingsClass, bool? FreedPaymentsToSavings)
{
    /// <summary>Class rates merged key by key, every other field replaced when set.</summary>
    public ProjectionAssumptions Apply(ProjectionAssumptions household)
    {
        var rates = household.ClassRates.ToDictionary(r => r.Key, r => r.Value);
        foreach (var (@class, rate) in ClassRates ?? new Dictionary<string, decimal>())
            rates[@class] = rate;
        return new ProjectionAssumptions(
            rates, Inflation ?? household.Inflation, MonthlySavings ?? household.MonthlySavings,
            SavingsClass ?? household.SavingsClass, FreedPaymentsToSavings ?? household.FreedPaymentsToSavings);
    }
}

/// <summary>Spec §6: the stored form of the horizon, <c>Years</c> or until <c>RetireOf</c> retires.</summary>
public sealed record ScenarioHorizon(int? Years, PersonId? RetireOf);

/// <summary>Spec §6: the document of a projection scenario.</summary>
public sealed record ProjectionScenario(int Version, ScenarioHorizon Horizon, ProjectionOverrides Assumptions, IReadOnlyList<ProjectionEvent> Events);
