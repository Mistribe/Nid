using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>Rows an item delete removes with it, by stable key (<c>flows</c>, <c>valuations</c>, …).</summary>
public sealed record DeletionCount(string Key, int Count);

/// <summary>What refuses an item delete while it stands, and the items it comes from when there are some.</summary>
public sealed record DeletionBlocker(string Key, int Count, IReadOnlyList<DeletionBlockerItem> Items);

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

    /// <summary>Tracked removes of this part's rows for the item; called inside the caller's unit of work.</summary>
    Task RemoveAsync(ItemAccess item, CancellationToken ct);
}

public interface IItemDeletions
{
    /// <summary>Null when the item does not exist. No access check (the command's IItemScoped did it).</summary>
    Task<ItemDeletionPreview?> PreviewAsync(ItemId id, CancellationToken ct);

    /// <summary>
    /// Inside the caller's unit of work (a savepoint of it): blockers again (<see cref="ItemCodes.InUse"/>), every part's
    /// <see cref="IItemDeletionPart.RemoveAsync"/>, then the registry delete (<see cref="ItemCodes.Changed"/>). A failure removes nothing.
    /// </summary>
    Task<Result> DeleteAsync(ItemId id, uint version, CancellationToken ct);
}

/// <summary>Scoped: the proposal being approved in this request, so the delete does not supersede it.</summary>
public interface IApprovingProposal
{
    Guid? Id { get; set; }
}
