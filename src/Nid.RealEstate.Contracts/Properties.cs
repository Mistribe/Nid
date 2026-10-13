using Nid.Core.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Contracts;

/// <summary>README §3: snake_case in the column and on the wire, through <see cref="LoanWire"/> (<c>primary_residence</c>).</summary>
public enum PropertyType { PrimaryResidence, SecondaryResidence, Rental, Parking, Land, Other }

/// <summary>Spec 3C1 §2: <c>yearly</c> is stale after 400 days, <c>manual</c> never — unless <c>stale_after_days</c> says otherwise.</summary>
public enum PropertyCadence { Yearly, Manual }

/// <summary>A property's own fields (spec 3C1 §2); amounts in the item's currency.</summary>
public sealed record PropertyDetails(
    PropertyType Type, LocalDate PurchaseDate, decimal PurchasePrice, decimal NotaryFees, decimal AgencyFees,
    decimal OtherAcquisitionCosts, decimal? SurfaceM2, string? LocationLabel, PropertyCadence Cadence, int? StaleAfterDays,
    LocalDate? RentalStartsOn, string? Note);

/// <summary>README §4: a property's flow categories by kind, in the order the real cost lists them.</summary>
public static class PropertyCategories
{
    public const string Works = "works";
    public const string Rent = "rent";

    public static readonly IReadOnlyList<string> Expense = [Works, "property_tax", "condo_fees", "insurance", "energy", "maintenance", "other"];
    public static readonly IReadOnlyList<string> Income = [Rent, "other_income"];
}

public static class PropertyCodes
{
    public const string InvalidProperty = "realestate.invalid_property";
    public const string InvalidPropertyField = "realestate.invalid_property_field";
    public const string InvalidCategory = "realestate.invalid_category";
    public const string PropertyConflict = "realestate.property_conflict";
    public const string RecurringCostConflict = "realestate.recurring_cost_conflict";
}

/// <summary>Spec 3C1 §6.</summary>
public static class PropertyWarnings
{
    public const string NoValuation = "no_valuation";
    public const string ValuationStale = "valuation_stale";
    public const string LoanOtherCurrency = "loan_other_currency";
    public const string FxMissing = "fx_missing";
    public const string VisibilityMismatch = "visibility_mismatch";

    /// <summary>Spec 3C2 §4: a property of type <c>rental</c> with no tenancy and no <c>rental_starts_on</c>.</summary>
    public const string RentalWithoutTenancy = "rental_without_tenancy";
}

/// <summary>One component of the real cost: <c>acquisition</c>, <c>loan_interest</c>, <c>loan_insurance</c>, <c>loan_fees</c>, or an expense category.</summary>
public sealed record CostLine(string Code, decimal Amount);

/// <summary>
/// A linked loan the reader can read, at d (spec 3C1 §5): its figures in its own currency, and the rate to the property's
/// currency — 1 in the same currency, null when the rate is missing at d.
/// </summary>
public sealed record LinkedLoan(ItemId Id, bool OtherCurrency, bool Private, LoanFigures Figures, decimal? Rate);

/// <summary>A linked loan's balance at d, and in the property's currency (null when its rate is missing).</summary>
public sealed record LinkedLoanBalance(ItemId Id, decimal Balance, decimal? Converted);

public sealed record PropertyWarning(string Code, ItemId? LoanId = null);

/// <summary>Spec 3C2 §2: a planned cost; it generates no flow (§1.1). <c>Every</c> is months or years; the range is [StartsOn, EndsOn).</summary>
public sealed record RecurringCost(string Category, decimal Amount, Duration Every, LocalDate StartsOn, LocalDate? EndsOn, string? Note);

/// <summary>Spec 3C2 §2: a lease on the property, [StartsOn, EndsOn); a flat share is one tenancy at the total rent, its label never personal data.</summary>
public sealed record Tenancy(
    string Label, LocalDate StartsOn, LocalDate? EndsOn, decimal RentMonthly, decimal ChargesMonthly, decimal? Deposit, string? Note);

/// <summary>
/// Spec 3C2 §4 over W = (d − 12 months, d]. <c>RentedSince</c> is R. Rates are fractions in full precision; vacancy is null when R
/// is null or after d, the yields when R is null or after d − 12 months.
/// </summary>
public sealed record RentalFigures(
    LocalDate? RentedSince, decimal ExpectedRent, decimal RentReceived, decimal Arrears, int? VacantDays, decimal? VacancyRate,
    decimal? GrossYield, decimal? NetYieldBeforeTax);

/// <summary>Specs 3C1 §5 and 3C2 §4 at d. <c>Value</c> null: no estimate yet — unknown, never 0 nor the purchase price.</summary>
public sealed record PropertyFigures(
    decimal? Value, LocalDate? ValueOn, bool Stale, decimal CostPrice, decimal? LatentGain, decimal LinkedBalance, decimal? NetValue,
    decimal RealCost, IReadOnlyList<CostLine> RealCostLines, decimal NetCostOfRents, IReadOnlyList<LinkedLoanBalance> Loans,
    IReadOnlyList<PropertyWarning> Warnings, RentalFigures Rental);

/// <summary>A planned cost as stored; <c>Version</c> is its row's <c>xmin</c>.</summary>
public sealed record StoredRecurringCost(Guid Id, RecurringCost Cost, uint Version);

/// <summary>A tenancy as stored; <c>Version</c> is its row's <c>xmin</c>.</summary>
public sealed record StoredTenancy(Guid Id, Tenancy Tenancy, uint Version);

/// <summary>
/// A property as stored. <c>Version</c> is its row's <c>xmin</c>: it moves when one of its fields changes, not on a rename, nor when
/// a cost or a tenancy is written (each has its own). Costs and tenancies by start date, then id.
/// </summary>
public sealed record StoredProperty(
    ItemId ItemId, PropertyDetails Details, uint Version, IReadOnlyList<StoredRecurringCost> Costs, IReadOnlyList<StoredTenancy> Tenancies);

public interface IPropertyStore
{
    Task CreateAsync(ItemId item, PropertyDetails details, CancellationToken ct);

    /// <summary>Null when the item is not a property (or does not exist).</summary>
    Task<StoredProperty?> FindAsync(ItemId item, CancellationToken ct);

    Task<IReadOnlyList<StoredProperty>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    /// <summary>Locks the property's row until the transaction ends: a PATCH compares its version with what is now stored (precision 4).</summary>
    Task LockAsync(ItemId item, CancellationToken ct);

    /// <summary>Every field in place.</summary>
    Task UpdateAsync(ItemId item, PropertyDetails details, CancellationToken ct);

    Task AddCostAsync(ItemId property, Guid id, RecurringCost cost, CancellationToken ct);

    /// <summary>Every value in place; the cost's version always moves.</summary>
    Task UpdateCostAsync(ItemId property, Guid id, RecurringCost cost, CancellationToken ct);

    Task DeleteCostAsync(ItemId property, Guid id, CancellationToken ct);

    Task AddTenancyAsync(ItemId property, Guid id, Tenancy tenancy, CancellationToken ct);

    /// <summary>Every value in place; the tenancy's version always moves.</summary>
    Task UpdateTenancyAsync(ItemId property, Guid id, Tenancy tenancy, CancellationToken ct);

    Task DeleteTenancyAsync(ItemId property, Guid id, CancellationToken ct);
}
