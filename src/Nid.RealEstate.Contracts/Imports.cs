using Nid.Core.Contracts;

namespace Nid.RealEstate.Contracts;

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
