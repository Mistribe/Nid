using Nid.Assets.Contracts;
using Nid.Assets.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Assets.Application;

/// <summary>
/// Open to every adult (<c>ICreatesItem</c>). A value is always present: the purchase price in <paramref name="Details"/>, or
/// <paramref name="CurrentValue"/>, recorded as a valuation on <paramref name="CurrentValueOn"/> (today when null).
/// <paramref name="Currency"/> null: the household's reference currency.
/// </summary>
public sealed record CreateAssetCommand(
    string Name, AssetDetails Details, decimal? CurrentValue, LocalDate? CurrentValueOn, Currency? Currency, Visibility Visibility,
    IReadOnlyList<OwnerShare> Owners, ShareLevel ShareLevel = ShareLevel.View) : ICommand<ItemId>, ICreatesItem, ITransactional;

/// <summary>Replaces every field, kind included; the visibility and the owners stay. <paramref name="Version"/>: the one read with the asset.</summary>
public sealed record EditAssetCommand(ItemId ItemId, string Name, AssetDetails Details, uint Version) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ArchiveAssetCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record UnarchiveAssetCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped, ITransactional
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>An asset created by mistake, deleted with its valuations. <paramref name="Version"/>: the one the preview read.</summary>
public sealed record DeleteAssetCommand(ItemId ItemId, uint Version) : ICommand<ItemId>, IItemScoped, IConfirmedInBrowser
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateAssetCommand, ItemId>]
public sealed class CreateAssetHandler(
    IItemRegistry items, IAssetRecordStore assets, IValuations valuations, IHouseholdCalendar calendar, IHouseholdSettings household)
    : IRequestHandler<CreateAssetCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateAssetCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var failure = AssetCheck.Name(command.Name, command.Details.Note) ?? AssetRules.Check(command.Details, today)
            ?? (command.CurrentValue is { } value ? AssetCheck.Positive(value, "currentValue") : null)
            ?? (AssetRules.HasValue(command.Details, command.CurrentValue is null ? 0 : 1) ? null : AssetCheck.ValueRequired("currentValue"));
        if (failure is not null)
            return Result.Failure<ItemId>(failure);

        var currency = command.Currency ?? await household.ReferenceCurrencyAsync(ct);
        var created = await items.CreateAsync(
            new NewItem("assets", "asset", command.Name, command.Visibility, currency.Code, command.Owners, command.ShareLevel), ct);
        if (!created.TryGetValue(out var id))
            return Result.Failure<ItemId>(FieldErrors.At(created.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException(), "owners"));
        await assets.AddAsync(id, command.Details, ct);
        if (command.CurrentValue is { } current)
        {
            var recorded = await valuations.RecordAsync(
                new NewValuation(id, command.CurrentValueOn ?? today, AssetCheck.MoneyOf(current, currency.Code), null), ct);
            if (recorded.TryGetFailure(out failure))
                return Result.Failure<ItemId>(AssetValuations.Located(failure!, "currentValueOn")); // rolls the transaction back: nothing is left
        }
        return Result.Success(id);
    }
}

[RequestHandler<EditAssetCommand, ItemId>]
public sealed class EditAssetHandler(
    IItemRegistry items, IAssetRecordStore assets, IValuations valuations, IHouseholdCalendar calendar)
    : IRequestHandler<EditAssetCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditAssetCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<ItemId>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        if (found.Asset.Version != command.Version)
            return Conflict();

        var failure = AssetCheck.Name(command.Name, command.Details.Note) ?? AssetRules.Check(command.Details, await calendar.TodayAsync(ct));
        if (failure is null && command.Details.PurchasePrice is null)
        {
            var series = await valuations.SeriesAsync(command.ItemId, AssetCheck.Everything, ct);
            if (!series.TryGetValue(out var list))
                return Result.Failure<ItemId>(series.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
            failure = AssetRules.HasValue(command.Details, list!.Count) ? null : AssetCheck.ValueRequired("purchasePrice");
        }
        if (failure is not null)
            return Result.Failure<ItemId>(failure);

        var item = found.Item;
        if (command.Name != item.DisplayName)
        {
            var renamed = await items.ReplaceAsync(command.ItemId, command.Name, item.Visibility,
                item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (renamed.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }
        return await assets.UpdateAsync(command.ItemId, command.Details, command.Version, ct) ? Result.Success(command.ItemId) : Conflict();
    }

    private static Result<ItemId> Conflict()
        => Result.Failure<ItemId>(new ConflictFailure("The asset changed since it was read.") { Code = AssetsCodes.Conflict });
}

[RequestHandler<ArchiveAssetCommand, ItemId>]
public sealed class ArchiveAssetHandler(IAssetRecordStore assets, IItemRegistry items, IClock clock) : IRequestHandler<ArchiveAssetCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ArchiveAssetCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        return (await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct)).Map(() => command.ItemId);
    }
}

[RequestHandler<UnarchiveAssetCommand, ItemId>]
public sealed class UnarchiveAssetHandler(IAssetRecordStore assets, IItemRegistry items) : IRequestHandler<UnarchiveAssetCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(UnarchiveAssetCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        return (await items.UnarchiveAsync(command.ItemId, ct)).Map(() => command.ItemId);
    }
}

[RequestHandler<DeleteAssetCommand, ItemId>]
public sealed class DeleteAssetHandler(IItemDeletions deletions) : IRequestHandler<DeleteAssetCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteAssetCommand command, CancellationToken ct = default)
        => (await deletions.DeleteAsync(command.ItemId, "assets", "asset", command.Version, ct)).Map(() => command.ItemId);
}
