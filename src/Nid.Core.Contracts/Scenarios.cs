using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>Property purchase scenario spec §3: the kinds of scenario; rates are fractions, amounts are in the reference currency.</summary>
public static class ScenarioKinds
{
    public const string PropertyPurchase = "property_purchase";

    /// <summary>Net worth projection spec §6: a projection scenario, stored beside the purchases.</summary>
    public const string Projection = "projection";
}

public enum PurchaseUse { Primary, Rental }

public enum DeferralKind { Partial, Total }

public enum ScenarioInsuranceKind { RateOnInitial, FixedMonthly }

public sealed record ScenarioDeferral(DeferralKind Kind, int Months);

/// <summary><c>Value</c> is an annual rate for <c>RateOnInitial</c>, a monthly amount for <c>FixedMonthly</c>.</summary>
public sealed record ScenarioInsurance(ScenarioInsuranceKind Kind, decimal Value);

public sealed record ScenarioLoan(
    string Name, decimal Amount, decimal Rate, int Months, ScenarioDeferral? Deferral, ScenarioInsurance Insurance,
    decimal FileFees, decimal GuaranteeFees);

public sealed record FundingLine(ItemId Item, decimal Amount);

public sealed record ScenarioAssumptions(
    int HorizonYears, IReadOnlyDictionary<string, decimal> ClassRates, decimal? PropertyGrowth, decimal MonthlySavings, string SavingsClass);

/// <summary>The document of a property purchase scenario. <c>RentalRegime</c> is <c>"micro"</c>, <c>"real"</c> or null (the tax profile's).</summary>
public sealed record PropertyPurchase(
    int Version, PurchaseUse Use, LocalDate PurchaseOn, decimal Price, decimal NotaryFees, decimal AgencyFees, decimal OtherCosts,
    decimal MonthlyCharges, decimal CurrentRent, decimal ExpectedRent, int VacancyMonths, string? RentalRegime,
    IReadOnlyList<FundingLine> Funding, IReadOnlyList<ScenarioLoan> Loans, ScenarioAssumptions Assumptions);

public sealed record SimulatedPayment(int Number, LocalDate DueDate, decimal Payment, decimal Interest, decimal Principal, decimal Insurance, decimal Balance);

/// <summary>A loan of the scenario read by the real engine; <c>Rows</c> in due order.</summary>
public sealed record SimulatedLoan(string Name, IReadOnlyList<SimulatedPayment> Rows, decimal Fees);

public interface ILoanSimulator
{
    /// <summary>Spec §3: disbursed on <paramref name="purchaseOn"/>, first payment a month later; a refusal names <c>loans[i]</c>.</summary>
    Result<IReadOnlyList<SimulatedLoan>> Simulate(Currency currency, LocalDate purchaseOn, IReadOnlyList<ScenarioLoan> loans);
}

/// <summary>Spec §4.2 step 3: month 0 is this month at today's figures; amounts converted to the reference at today's rate.</summary>
public sealed record RealEstateMonth(YearMonth Month, decimal PropertyValue, decimal Debt, decimal LoanPayments);

/// <summary>Projection spec §3.5 and §5.2: a running visible loan's last schedule row, and the property it is linked to.</summary>
public sealed record LoanEnd(ItemId Loan, string Name, LocalDate On, ItemId? Property);

/// <summary>Projection spec §3.5: a visible property's last loan ends, every one of its loans ending within the projection.</summary>
public sealed record PropertyFree(ItemId Property, string Name, LocalDate On);

/// <summary>
/// Projection spec §4.2: an early repayment of <c>Amount</c> (reference currency) on <c>On</c>, split by <c>Split</c>
/// (<see cref="ProjectionSplits"/>; <c>Loan</c> with <c>chosen</c>) and shortening by <c>Effect</c> (<see cref="ProjectionEffects"/>).
/// </summary>
public sealed record ProjectedRepayment(Guid Event, LocalDate On, decimal Amount, string Split, ItemId? Loan, string Effect);

/// <summary>Projection spec §4.3: the sale of <c>Property</c> on <c>On</c>; <c>Price</c> null is its projected value then. Reference currency.</summary>
public sealed record ProjectedSale(Guid Event, ItemId Property, LocalDate On, decimal? Price, decimal Costs);

/// <summary>Projection spec §5.2: the scenario's events RealEstate applies; each targets an item the reader sees.</summary>
public sealed record RealEstateEvents(IReadOnlyList<ProjectedRepayment> Repayments, IReadOnlyList<ProjectedSale> Sales)
{
    public static readonly RealEstateEvents None = new([], []);
}

/// <summary>
/// A sale at grid month <c>Month</c>: <c>Proceeds</c> = price − costs − the settled balance of its loans (negative possible);
/// <c>MonthlyNet</c> = its monthly recurring costs − its monthly rent today, which stop with the sale. Reference currency.
/// </summary>
public sealed record SaleOutcome(Guid Event, int Month, decimal Proceeds, decimal MonthlyNet);

/// <summary>A repayment at grid month <c>Month</c>: <c>Applied</c> is what the eligible loans took, at most the amount asked (capped at their balance).</summary>
public sealed record RepaymentOutcome(Guid Event, int Month, decimal Applied);

/// <summary>
/// <c>LoanEnds</c> and <c>PropertiesFree</c> are the projection's milestones (projection spec §5.2); the purchase scenario does not
/// read them. <c>Sales</c> and <c>Repayments</c> are the outcomes of the events within the grid, in the events' order.
/// </summary>
public sealed record RealEstateProjection(IReadOnlyList<RealEstateMonth> Months, IReadOnlyList<string> MissingCurrencies)
{
    public IReadOnlyList<LoanEnd> LoanEnds { get; init; } = [];

    public IReadOnlyList<PropertyFree> PropertiesFree { get; init; } = [];

    public IReadOnlyList<SaleOutcome> Sales { get; init; } = [];

    public IReadOnlyList<RepaymentOutcome> Repayments { get; init; } = [];
}

public interface IRealEstateProjection
{
    /// <summary>The purchase scenario's call: no event.</summary>
    Task<Result<RealEstateProjection>> ProjectAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, int months, decimal annualGrowth, CancellationToken ct)
        => ProjectAsync(reader, scope, today, months, annualGrowth, RealEstateEvents.None, ct);

    /// <summary>
    /// Projection spec §5.2: point k is the last day of this month + k; an event dated in month k applies at k (one dated before
    /// today, today), one beyond the grid is ignored, as is one on an item the reader cannot see.
    /// </summary>
    Task<Result<RealEstateProjection>> ProjectAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, int months, decimal annualGrowth, RealEstateEvents events, CancellationToken ct);
}

/// <summary>
/// Spec §5: a scenario's document, stored as a JSON row keyed by its registry item. <c>Scope</c> is what it is computed on.
/// <c>Document</c> is set for a purchase, <c>Projection</c> for a projection scenario (projection spec §6), by <c>Kind</c>.
/// </summary>
public sealed record StoredScenario(ItemId Item, string Kind, VisibilityScope Scope, PropertyPurchase? Document, ProjectionScenario? Projection = null);

public interface IScenarioStore
{
    Task<StoredScenario?> FindAsync(ItemId id, CancellationToken ct);

    /// <summary>The scenarios that exist among <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<StoredScenario>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task SaveAsync(StoredScenario scenario, CancellationToken ct);
}
