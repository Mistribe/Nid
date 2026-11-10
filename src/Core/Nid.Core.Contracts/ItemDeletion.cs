using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>Rows an item delete removes with it, by stable key (<c>flows</c>, <c>valuations</c>, …).</summary>
public sealed record DeletionCount(string Key, int Count);

/// <summary>What refuses an item delete while it stands, and the items it comes from when there are some.</summary>
public sealed record DeletionBlocker(string Key, int Count, IReadOnlyList<DeletionBlockerItem> Items);

/// <summary><paramref name="Id"/> must be a <c>core.item</c> id: the names are filtered by the reader's access to it.</summary>
public sealed record DeletionBlockerItem(Guid Id, string Name);

/// <summary><paramref name="Version"/> is the <c>core.item</c> xmin the delete must send back.</summary>
public sealed record ItemDeletionPreview(string Name, uint Version, bool Imported,
    IReadOnlyList<DeletionCount> Removes, IReadOnlyList<DeletionBlocker> Blockers);

/// <summary>One module's share of an item delete. Asked for every item: a part returns nothing for items it has no rows for.</summary>
public interface IItemDeletionPart
{
    Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct);

    Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct);

    /// <summary>True when the item's module row carries an import mark (only the owning module answers true).</summary>
    Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct);

    /// <summary>
    /// Tracked removes of this part's rows for the item; called inside the caller's unit of work. RemoveAsync also runs in dry runs:
    /// never write outside the unit of work (no own scope, no file or blob deletes); a notice waits for <c>IUnitOfWork.AfterCommit</c>.
    /// </summary>
    Task RemoveAsync(ItemAccess item, CancellationToken ct);

    /// <summary>Core's own part removes the item's facts; it runs after every module's part, so a part may still read them.</summary>
    bool RunsLast => false;
}

/// <summary>A module's delete endpoints call these with their own <c>module</c> (and <c>kind</c> when one endpoint serves one kind only).</summary>
public interface IItemDeletions
{
    /// <summary>Not found when the item does not exist or is not of <paramref name="module"/> (and <paramref name="kind"/>). No access check (the request's IItemScoped did it).</summary>
    Task<Result<ItemDeletionPreview>> PreviewAsync(ItemId id, string module, string? kind, CancellationToken ct);

    /// <summary>
    /// Not found as <see cref="PreviewAsync"/>. In a unit of work of its own, or a savepoint of the caller's: blockers again
    /// (<see cref="ItemCodes.InUse"/>), every part's <see cref="IItemDeletionPart.RemoveAsync"/>, then the registry delete
    /// (<see cref="ItemCodes.Changed"/>). A failure removes nothing.
    /// </summary>
    Task<Result> DeleteAsync(ItemId id, string module, string? kind, uint version, CancellationToken ct);
}

/// <summary>Scoped: the proposal being approved in this request, so the delete does not supersede it.</summary>
public interface IApprovingProposal
{
    Guid? Id { get; set; }
}
