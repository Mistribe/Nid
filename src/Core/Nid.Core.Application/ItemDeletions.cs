using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application;

/// <summary>#23: an item deleted with everything every module holds for it, after a preview of what goes.</summary>
public sealed class ItemDeletions(IItemRegistry registry, IEnumerable<IItemDeletionPart> parts, IUnitOfWork unitOfWork) : IItemDeletions
{
    public async Task<ItemDeletionPreview?> PreviewAsync(ItemId id, CancellationToken ct)
    {
        if (await registry.FindAsync(id, ct) is not { } item)
            return null;

        var removes = new List<DeletionCount>();
        var blockers = new List<DeletionBlocker>();
        var imported = false;
        foreach (var part in parts)
        {
            removes.AddRange((await part.CountAsync(item, ct)).Where(c => c.Count > 0));
            blockers.AddRange(await part.BlockersAsync(item, ct));
            imported |= await part.ImportedAsync(item, ct);
        }

        return new ItemDeletionPreview(item.DisplayName, item.Version, imported, removes, blockers);
    }

    public async Task<Result> DeleteAsync(ItemId id, uint version, CancellationToken ct)
    {
        if (await registry.FindAsync(id, ct) is not { } item)
            return Result.Failure(new NotFoundFailure("item", id.ToString()));

        // A savepoint inside the caller's unit of work: a refusal after the parts removed their rows takes nothing with it.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        foreach (var part in parts)
            if ((await part.BlockersAsync(item, ct)).Count > 0)
                return Result.Failure(new Failure(ItemCodes.InUse, "Something still refers to this item.", null));

        foreach (var part in parts)
            await part.RemoveAsync(item, ct);

        var deleted = await registry.DeleteAsync(id, version, ct);
        if (deleted.IsSuccess)
            await transaction.CommitAsync(ct);
        return deleted;
    }
}

public sealed class ApprovingProposal : IApprovingProposal
{
    public Guid? Id { get; set; }
}
