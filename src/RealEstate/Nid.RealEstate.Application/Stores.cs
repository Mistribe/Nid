using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Application;

/// <summary>A planned cost as stored; <c>Version</c> is its row's <c>xmin</c>.</summary>
public sealed record StoredRecurringCost(Guid Id, RecurringCost Cost, uint Version);

/// <summary>A tenancy as stored; <c>Version</c> is its row's <c>xmin</c>; <c>RevisedFrom</c> is the tenancy a rent revision continues.</summary>
public sealed record StoredTenancy(Guid Id, Tenancy Tenancy, uint Version, Guid? RevisedFrom = null);

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

    /// <summary>Those of <paramref name="items"/> that have a tenancy whose label contains the text (case-insensitive, literal).</summary>
    Task<IReadOnlyList<ItemId>> WithTenancyLabelAsync(IReadOnlyCollection<ItemId> items, string labelContains, CancellationToken ct);

    /// <summary>Locks the property's row until the transaction ends: a PATCH compares its version with what is now stored (precision 4).</summary>
    Task LockAsync(ItemId item, CancellationToken ct);

    /// <summary>Every field in place.</summary>
    Task UpdateAsync(ItemId item, PropertyDetails details, CancellationToken ct);

    /// <summary>Property return spec §3: null when none are stored.</summary>
    Task<ReturnAssumptions?> FindReturnAssumptionsAsync(ItemId item, CancellationToken ct);

    /// <summary>Property return spec §3: null clears them, back to the defaults.</summary>
    Task SaveReturnAssumptionsAsync(ItemId item, ReturnAssumptions? assumptions, CancellationToken ct);

    Task AddCostAsync(ItemId property, Guid id, RecurringCost cost, CancellationToken ct);

    /// <summary>Every value in place; the cost's version always moves.</summary>
    Task UpdateCostAsync(ItemId property, Guid id, RecurringCost cost, CancellationToken ct);

    Task DeleteCostAsync(ItemId property, Guid id, CancellationToken ct);

    Task AddTenancyAsync(ItemId property, Guid id, Tenancy tenancy, CancellationToken ct, Guid? revisedFrom = null);

    /// <summary>Every value in place; the tenancy's version always moves.</summary>
    Task UpdateTenancyAsync(ItemId property, Guid id, Tenancy tenancy, CancellationToken ct);

    Task DeleteTenancyAsync(ItemId property, Guid id, CancellationToken ct);
}

/// <summary>
/// A change: a null field is left as it is; <c>Clear*</c> empties an optional one. <c>PropertyId</c> links the loan (spec 3C1
/// §1.5), <c>EnvelopeId</c> to an SCPI envelope (#180); <c>Source</c> chooses the schedule's reference (spec 3F2 §3).
/// </summary>
public sealed record LoanDetailsChange(
    string? Lender = null, bool ClearLender = false, string? Note = null, bool ClearNote = false,
    ItemId? PropertyId = null, bool ClearProperty = false, ScheduleSource? Source = null,
    ItemId? EnvelopeId = null, bool ClearEnvelope = false);

public interface ILoanStore
{
    Task CreateAsync(ItemId item, string? lender, string? note, ItemId? property, LoanContract contract, CancellationToken ct, ItemId? envelope = null);

    /// <summary>Null when the item is not a loan (or does not exist).</summary>
    Task<StoredLoan?> FindAsync(ItemId item, CancellationToken ct);

    Task<IReadOnlyList<StoredLoan>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    /// <summary>The loans linked to these properties, whoever may read them: the caller filters (spec 3C1 §1.7).</summary>
    Task<IReadOnlyList<StoredLoan>> FindLinkedAsync(IReadOnlyCollection<ItemId> properties, CancellationToken ct);

    /// <summary>#180: the ids of the loans that finance this envelope, whoever may read them: the caller filters.</summary>
    Task<IReadOnlyList<ItemId>> FinancingAsync(ItemId envelope, CancellationToken ct);

    /// <summary>Locks the loan's row until the transaction ends: a loan's writes run one after the other (precision 5).</summary>
    Task LockAsync(ItemId item, CancellationToken ct);

    Task UpdateDetailsAsync(ItemId item, LoanDetailsChange change, CancellationToken ct);

    /// <summary>The terms in place, the phases and insurances deleted and written again; the loan's version always moves.</summary>
    Task ReplaceContractAsync(ItemId item, LoanContract contract, CancellationToken ct);

    /// <summary>
    /// Spec 3F2 §3: the bank's table replaced whole by <paramref name="rows"/>, their ids and marks as given; the loan's version
    /// always moves. An empty table returns the loan to the calculation (<c>schedule_source</c> back to <c>computed</c>).
    /// </summary>
    Task ReplaceBankScheduleAsync(ItemId loan, IReadOnlyList<StoredBankRow> rows, CancellationToken ct);

    Task AddFeeAsync(ItemId loan, Guid id, LoanFee fee, string? note, CancellationToken ct);

    /// <summary>Every value in place; the fee's version always moves.</summary>
    Task UpdateFeeAsync(ItemId loan, Guid id, LoanFee fee, string? note, CancellationToken ct);

    Task DeleteFeeAsync(ItemId loan, Guid id, CancellationToken ct);

    Task AddRepaymentAsync(ItemId loan, Guid id, EarlyRepayment repayment, Guid? feeId, string? note, CancellationToken ct);

    /// <summary>Every value in place; the repayment's version always moves.</summary>
    Task UpdateRepaymentAsync(ItemId loan, Guid id, EarlyRepayment repayment, Guid? feeId, string? note, CancellationToken ct);

    Task DeleteRepaymentAsync(ItemId loan, Guid id, CancellationToken ct);
}

/// <summary>Spec 3F1 §2: the module's tables an import marks with the file id an entity came from (import-format.md §8).</summary>
public enum EstateTable { Property, RecurringCost, Tenancy, Loan, Phase, Insurance, Fee, Event, BankRow }

/// <summary>
/// A row imported under a file id: its id (the item's for a property or a loan), the property or loan it belongs to (itself
/// for those), the entity's hash, and who imported it.
/// </summary>
public sealed record ImportedEstateRow(Guid Id, ItemId Item, string Hash, PersonId ImportedBy);

public interface IRealEstateImports
{
    /// <summary>Every importer's rows of <paramref name="table"/> under these file ids: the import section decides which one the importer may see.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedEstateRow>>> FindAsync(EstateTable table, IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>
    /// The marks of the rows of <paramref name="table"/> on these properties or loans (the items themselves for <c>Property</c>
    /// and <c>Loan</c>), by row id; a row made in the app has none. A phase's, an insurance's or a bank row's mark is read with its loan
    /// (<see cref="LoanPhaseEntry.Import"/>).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ImportMark>> MarksAsync(EstateTable table, IReadOnlyCollection<ItemId> items, CancellationToken ct);

    /// <summary>Marks a row just written (by its id; the item's for a property or a loan), in the import's transaction.</summary>
    Task MarkAsync(EstateTable table, Guid id, ImportMark mark, CancellationToken ct);
}
