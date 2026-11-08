using Nid.Core.Contracts;
using Nid.Income.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Income.Application;

internal static class SourceCheck
{
    /// <summary>The source and its item; an item that is not a source answers as a missing source.</summary>
    public static async Task<Result<(IncomeSource Source, ItemAccess Item)>> LoadAsync(
        ISourceStore sources, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var source = await sources.FindAsync(id, ct);
        var item = source is null ? null : await items.FindAsync(id, ct);
        return source is null || item is null
            ? Result.Failure<(IncomeSource, ItemAccess)>(new NotFoundFailure("income source", id.ToString()))
            : Result.Success((source, item));
    }

    /// <summary>Spec §4: an ended source refuses every write but its reopening.</summary>
    public static Result RequireOpen(ItemAccess item)
        => item.ArchivedAt is null ? Result.Success()
            : Result.Failure(new Failure(IncomeCodes.SourceEnded, "This source has ended: reopen it first.", null));

    /// <summary>A source's item always has a currency (it is created with one); its absence is a broken invariant.</summary>
    public static Currency CurrencyOf(ItemAccess item)
        => Currency.TryCreate(item.Currency ?? "", out var currency)
            ? currency
            : throw new InvalidOperationException($"Income source {item.Id} has no currency.");
}
