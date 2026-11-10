using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application;

/// <summary>#23: an item deleted with everything every module holds for it, after a preview of what goes.</summary>
public sealed class ItemDeletions(IItemRegistry registry, IEnumerable<IItemDeletionPart> parts, IUnitOfWork unitOfWork) : IItemDeletions
{
    private async Task<ItemAccess?> FindAsync(ItemId id, string module, string? kind, CancellationToken ct)
        => await registry.FindAsync(id, ct) is { } item && item.Module == module && (kind is null || item.Kind == kind) ? item : null;

    private static NotFoundFailure NotFound(ItemId id, string module, string? kind) => new(kind ?? module, id.ToString());

    public async Task<Result<ItemDeletionPreview>> PreviewAsync(ItemId id, string module, string? kind, CancellationToken ct)
    {
        if (await FindAsync(id, module, kind, ct) is not { } item)
            return Result.Failure<ItemDeletionPreview>(NotFound(id, module, kind));

        var removes = new List<DeletionCount>();
        var blockers = new List<DeletionBlocker>();
        var imported = false;
        foreach (var part in parts)
        {
            removes.AddRange((await part.CountAsync(item, ct)).Where(c => c.Count > 0));
            blockers.AddRange(await part.BlockersAsync(item, ct));
            imported |= await part.ImportedAsync(item, ct);
        }

        return Result.Success(new ItemDeletionPreview(item.DisplayName, item.Version, imported, removes, blockers));
    }

    public async Task<Result> DeleteAsync(ItemId id, string module, string? kind, uint version, CancellationToken ct)
    {
        if (await FindAsync(id, module, kind, ct) is not { } item)
            return Result.Failure(NotFound(id, module, kind));

        // The request's own unit of work (a plain DELETE), or a savepoint of the caller's (an approval, a dry run): a refusal after the
        // parts removed their rows takes nothing with it.
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
