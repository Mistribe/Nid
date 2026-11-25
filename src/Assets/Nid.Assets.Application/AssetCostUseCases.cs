using System.Globalization;
using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Assets.Application;

/// <summary>
/// #649: a running cost as sent. It is an expense; <paramref name="ExternalRef"/> (<c>bank:{lineId}</c>) is unique among the asset's flows,
/// <paramref name="DocumentId"/> its invoice (the host checked the actor reads it).
/// </summary>
public sealed record AssetCostDraft(LocalDate OccurredOn, decimal Amount, string? Category, string? Note, string? ExternalRef = null, Guid? DocumentId = null);

public sealed record RecordAssetCostCommand(ItemId ItemId, AssetCostDraft Cost) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record CorrectAssetCostCommand(ItemId ItemId, Guid FlowId, AssetCostDraft Cost) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record VoidAssetCostCommand(ItemId ItemId, Guid FlowId) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The costs of one year, newest first, with their total and the years that hold costs; <paramref name="Year"/> defaults to the household's current one.</summary>
public sealed record ListAssetCostsQuery(ItemId ItemId, int? Year) : IQuery<AssetCostsView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record AssetCostView(Guid Id, LocalDate OccurredOn, string Category, MoneyView Amount, string? Note, Guid? DocumentId, bool FromBank);

/// <param name="Years">Every year that holds a cost, newest first, and the one asked for.</param>
public sealed record AssetCostsView(int Year, IReadOnlyList<AssetCostView> Rows, MoneyView Total, IReadOnlyList<int> Years) : IExposesDocuments
{
    public IEnumerable<Guid> ExposedDocuments() => Rows.Select(r => r.DocumentId).OfType<Guid>();

    public object WithReadableDocuments(IReadOnlySet<Guid> readable)
        => this with { Rows = Rows.Select(r => r with { DocumentId = readable.Keep(r.DocumentId) }).ToList() };
}

internal static class AssetCosts
{
    private static string Amount(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The vehicle a cost is for: any other asset kind refuses it.</summary>
    public static async Task<Result<(AssetRecord Asset, ItemAccess Item)>> VehicleAsync(IAssetRecordStore assets, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, id, ct);
        return loaded.TryGetValue(out var found) && found.Asset.Details.Kind != AssetKinds.Vehicle
            ? Result.Failure<(AssetRecord, ItemAccess)>(new Failure(AssetsCodes.CostsVehicleOnly, "A running cost is for a vehicle only.", null))
            : loaded;
    }

    public static Result<NewFlow> FlowOf(ItemId item, AssetCostDraft draft, string? currency)
    {
        if ((AssetCheck.Note(draft.Note) ?? AssetCheck.Positive(draft.Amount, "amount")) is { } invalid)
            return Result.Failure<NewFlow>(invalid);
        var convention = new AssetsFactConventions().Conventions[0];
        if (convention.Refusal(FlowKind.Expense, draft.Category) is { } breaks)
            return Result.Failure<NewFlow>(FieldErrors.At(new Failure(convention.Code, breaks.Message, null), breaks.Member));
        return Result.Success(new NewFlow(item, draft.OccurredOn, FlowKind.Expense, AssetCheck.MoneyOf(draft.Amount, currency),
            draft.Category, draft.ExternalRef, draft.Note, DocumentId: draft.DocumentId));
    }

    /// <summary>The Core's date refusal names no field: the route's date field is given.</summary>
    public static Result<Guid> Answer(Result<Flow> written)
        => written.TryGetValue(out var flow)
            ? Result.Success(flow!.Id)
            : Result.Failure<Guid>(AssetValuations.Located(written.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException(), "occurredOn"));

    public static AssetCostView View(Flow f)
        => new(f.Id, f.OccurredOn, f.Category ?? AssetCostCategories.Other, new MoneyView(Amount(f.Amount.Amount), f.Amount.Currency.Code), f.Note, f.DocumentId,
            f.ExternalRef?.StartsWith("bank:", StringComparison.Ordinal) == true);
}

[RequestHandler<RecordAssetCostCommand, Guid>]
public sealed class RecordAssetCostHandler(IAssetRecordStore assets, IItemRegistry items, IFlows flows) : IRequestHandler<RecordAssetCostCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordAssetCostCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCosts.VehicleAsync(assets, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<Guid>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var flow = AssetCosts.FlowOf(command.ItemId, command.Cost, found.Item.Currency);
        return flow.TryGetValue(out var f)
            ? AssetCosts.Answer(await flows.RecordAsync(f!, ct))
            : Result.Failure<Guid>(flow.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
    }
}

[RequestHandler<CorrectAssetCostCommand, Guid>]
public sealed class CorrectAssetCostHandler(IAssetRecordStore assets, IItemRegistry items, IFlows flows) : IRequestHandler<CorrectAssetCostCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CorrectAssetCostCommand command, CancellationToken ct = default)
    {
        var loaded = await AssetCosts.VehicleAsync(assets, items, command.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<Guid>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var flow = AssetCosts.FlowOf(command.ItemId, command.Cost, found.Item.Currency);
        if (!flow.TryGetValue(out var f))
            return Result.Failure<Guid>(flow.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        // A correction without a reference keeps the stored one: a bank line's `bank:` reference still refuses a second cost.
        if (f!.ExternalRef is null && (await flows.FindManyAsync([command.FlowId], ct)).FirstOrDefault(x => x.Item == command.ItemId) is { ExternalRef: { } kept })
            f = f with { ExternalRef = kept };
        // The Core looks the flow up by (id, item): another asset's flow is core.fact_not_found.
        return AssetCosts.Answer(await flows.SupersedeAsync(command.ItemId, command.FlowId, f, ct));
    }
}

[RequestHandler<VoidAssetCostCommand, Guid>]
public sealed class VoidAssetCostHandler(IAssetRecordStore assets, IItemRegistry items, IFlows flows) : IRequestHandler<VoidAssetCostCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(VoidAssetCostCommand command, CancellationToken ct = default)
    {
        // Not checked as a vehicle: an asset that stopped being one can still lose a cost it recorded.
        if ((await AssetCheck.LoadAsync(assets, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var voided = await flows.VoidAsync(command.ItemId, command.FlowId, ct);
        return voided.TryGetFailure(out failure) ? Result.Failure<Guid>(failure!) : Result.Success(command.FlowId);
    }
}

[RequestHandler<ListAssetCostsQuery, AssetCostsView>]
public sealed class ListAssetCostsHandler(IAssetRecordStore assets, IItemRegistry items, IFlows flows, IHouseholdCalendar calendar)
    : IRequestHandler<ListAssetCostsQuery, AssetCostsView>
{
    public const int FirstYear = 1900;

    public async ValueTask<Result<AssetCostsView>> HandleAsync(ListAssetCostsQuery query, CancellationToken ct = default)
    {
        var loaded = await AssetCheck.LoadAsync(assets, items, query.ItemId, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<AssetCostsView>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var thisYear = (await calendar.TodayAsync(ct)).Year;
        var year = query.Year ?? thisYear;
        if (year is < FirstYear or > 9998)
            return Result.Failure<AssetCostsView>(AssetCheck.Shape("year", $"A year is {FirstYear} to 9998."));

        var all = await flows.ListAsync(query.ItemId, AssetCheck.Everything, new HashSet<FlowKind> { FlowKind.Expense }, ct);
        if (!all.TryGetValue(out var list))
            return Result.Failure<AssetCostsView>(all.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var range = DateRange.Create(new LocalDate(year, 1, 1), new LocalDate(year + 1, 1, 1));
        var total = await flows.SumAsync(query.ItemId, new HashSet<FlowKind> { FlowKind.Expense }, range.TryGetValue(out var days) ? days! : throw new InvalidOperationException(), ct);
        if (!total.TryGetValue(out var sum))
            return Result.Failure<AssetCostsView>(total.TryGetFailure(out var unread) ? unread! : throw new InvalidOperationException());

        var rows = list!.Where(f => f.OccurredOn.Year == year).OrderByDescending(f => f.OccurredOn).ThenByDescending(f => f.Id).Select(AssetCosts.View).ToList();
        var years = list!.Select(f => f.OccurredOn.Year).Append(thisYear).Append(year).Distinct().OrderDescending().ToList();
        return Result.Success(new AssetCostsView(year, rows, new MoneyView(sum.Amount.ToString("0.00", CultureInfo.InvariantCulture), sum.Currency.Code), years));
    }
}
