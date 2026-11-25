using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Assets.Application;

/// <summary>A correction is a record on the same date: the Core supersedes the earlier value.</summary>
public sealed record RecordAssetValuationCommand(ItemId ItemId, LocalDate AsOf, decimal Amount, string? Note) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record VoidAssetValuationCommand(ItemId ItemId, Guid ValuationId) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

internal static class AssetValuations
{
    /// <summary>The Core's date refusal names no field: the route's date field is given.</summary>
    public static Failure Located(Failure failure, string field)
        => failure.Code == CoreFactCodes.DateInFuture ? FieldErrors.At(failure, field) : failure;
}

[RequestHandler<RecordAssetValuationCommand, Guid>]
public sealed class RecordAssetValuationHandler(IAssetRecordStore assets, IItemRegistry items, IValuations valuations)
    : IRequestHandler<RecordAssetValuationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordAssetValuationCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<Guid>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        if (AssetCheck.Positive(command.Amount, "amount") is { } refused)
            return Result.Failure<Guid>(refused);
        var recorded = await valuations.RecordAsync(
            new NewValuation(command.ItemId, command.AsOf, AssetCheck.MoneyOf(command.Amount, found.Item.Currency), command.Note), ct);
        return recorded.TryGetValue(out var valuation)
            ? Result.Success(valuation!.Id)
            : Result.Failure<Guid>(AssetValuations.Located(recorded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException(), "asOf"));
    }
}

[RequestHandler<VoidAssetValuationCommand, Guid>]
public sealed class VoidAssetValuationHandler(IAssetRecordStore assets, IItemRegistry items, IValuations valuations)
    : IRequestHandler<VoidAssetValuationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(VoidAssetValuationCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<Guid>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var series = await valuations.SeriesAsync(command.ItemId, AssetCheck.Everything, ct);
        if (!series.TryGetValue(out var list))
            return Result.Failure<Guid>(series.TryGetFailure(out var unread) ? unread! : throw new InvalidOperationException());
        // A value is always present: the last valuation goes only when a purchase price stands behind it.
        if (found.Asset.Details.PurchasePrice is null && list!.Count == 1 && list[0].Id == command.ValuationId)
            return Result.Failure<Guid>(AssetCheck.ValueRequired("valuationId"));
        var voided = await valuations.VoidAsync(command.ItemId, command.ValuationId, ct);
        return voided.TryGetFailure(out var failure) ? Result.Failure<Guid>(failure!) : Result.Success(command.ValuationId);
    }
}
