using Nid.Assets.Application;
using Nid.Assets.Domain;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.RealEstateEndpoints;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary><c>/api/v1/assets</c>: other assets (a vehicle, a precious metal, a work of art, a collectible). Amounts are decimal strings.</summary>
public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/assets").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        // Filters, search and page are applied in SQL; the answer is one page, the total and the kinds the filter offers.
        api.MapGet("", async (string? scope, string? kind, Guid? owner, bool? archived, string? q, int? page, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new ListAssetsQuery(r.Scope(scope),
                new AssetFilter(string.IsNullOrEmpty(kind) ? null : kind, owner is { } o ? new PersonId(o) : null, archived ?? false, q, page ?? 1)), list => list, ct))
            .Returns<AssetListView>();

        api.MapPost("", async (CreateAssetBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetAssetQuery(new ItemId(id)), ct)).ToHttp(view => view))
            .Returns<AssetView>();

        api.MapPut("/{id:guid}", async (Guid id, EditAssetBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null ? reader.Problem() : (await invoker.InvokeAsync(command, ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapGet("/{id:guid}/deletion", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new AssetDeletionQuery(new ItemId(id)), ct)).ToHttp(preview => preview))
            .Returns<ItemDeletionPreview>();

        api.MapDelete("/{id:guid}", async (Guid id, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                // A missing version answers 422 at `version`: it would otherwise bind to 0 and pass for a conflict.
                if (version is null)
                {
                    var reader = new FieldReader();
                    reader.Add("version", "request.required", "The version read with the preview is required.");
                    return reader.Problem();
                }
                var result = await invoker.InvokeAsync(new DeleteAssetCommand(new ItemId(id), version.Value), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        api.MapPost("/{id:guid}/archive", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ArchiveAssetCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/unarchive", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new UnarchiveAssetCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/valuations", async (Guid id, AssetValuationBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var asOf = reader.Date(body.AsOf, "asOf");
                var amount = LoanBodies.Number(reader, body.Amount, "amount");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new RecordAssetValuationCommand(new ItemId(id), asOf!.Value, amount!.Value, body.Note), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapDelete("/{id:guid}/valuations/{valuationId:guid}", async (Guid id, Guid valuationId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new VoidAssetValuationCommand(new ItemId(id), valuationId), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        // #649: a vehicle's running costs, Core flows of the asset; the year's total is the server's.
        api.MapGet("/{id:guid}/costs", async (Guid id, int? year, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListAssetCostsQuery(new ItemId(id), year), ct)).ToHttp(view => view))
            .Returns<AssetCostsView>();

        api.MapPost("/{id:guid}/costs", async (Guid id, AssetCostBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var cost = body.Read(reader);
                if (cost is null)
                    return reader.Problem();
                return await UnreadableInvoiceAsync(invoker, cost.DocumentId, ct)
                    ?? (await invoker.InvokeAsync(new RecordAssetCostCommand(new ItemId(id), cost), ct)).ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/{id:guid}/costs/{flowId:guid}", async (Guid id, Guid flowId, AssetCostBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var cost = body.Read(reader);
                if (cost is null)
                    return reader.Problem();
                return await UnreadableInvoiceAsync(invoker, cost.DocumentId, ct)
                    ?? (await invoker.InvokeAsync(new CorrectAssetCostCommand(new ItemId(id), flowId, cost), ct)).ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/{id:guid}/costs/{flowId:guid}", async (Guid id, Guid flowId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new VoidAssetCostCommand(new ItemId(id), flowId), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>An invoice the actor cannot read is a missing document (404), before anything is written.</summary>
    private static async Task<Microsoft.AspNetCore.Http.IResult?> UnreadableInvoiceAsync(IInvoker invoker, Guid? documentId, CancellationToken ct)
        => documentId is { } d && !(await invoker.InvokeAsync(new GetDocumentQuery(d), ct)).TryGetValue(out _)
            ? Result.Failure<IdResponse>(new NotFoundFailure("document", d.ToString())).ToHttp(v => v)
            : null;

    /// <summary>
    /// The visibility is required, with no default on the wire. <c>purchasePrice</c> and <c>purchasedOn</c> go together; without a
    /// purchase, <c>currentValue</c> is required. <c>currency</c> missing: the household's.
    /// </summary>
    public sealed record CreateAssetBody(
        string? Name, string? Kind, string? PurchasePrice, string? PurchasedOn, string? Note, decimal? WeightGrams, decimal? Fineness,
        string? RegisteredOn, int? MileageKm, string? MileageOn, int? FiscalHp, string? Energy, decimal? DepreciationRate,
        string? CurrentValue, string? CurrentValueOn, string? Currency, string? Visibility, IReadOnlyList<OwnerBody>? Owners, string? ShareLevel = null)
    {
        internal CreateAssetCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            var level = reader.ShareLevel(ShareLevel, "shareLevel");
            Nid.Core.Contracts.Currency? currency = null;
            if (Currency is not null)
            {
                if (Nid.Core.Contracts.Currency.TryCreate(Currency, out var parsed))
                    currency = parsed;
                else
                    reader.Add("currency", PersonCodes.UnknownCurrency, "A currency is a three-letter ISO 4217 code, in capitals.");
            }
            var owners = LoanBodies.Borrowers(reader, Owners, "owners");
            var details = AssetBodies.Details(reader, Kind, PurchasePrice, PurchasedOn, Note, WeightGrams, Fineness,
                RegisteredOn, MileageKm, MileageOn, FiscalHp, Energy, DepreciationRate);
            var current = LoanBodies.Number(reader, CurrentValue, "currentValue", required: false);
            var currentOn = reader.Date(CurrentValueOn, "currentValueOn", required: false);
            return reader.Ok ? new CreateAssetCommand(Name ?? "", details, current, currentOn, currency, visibility!.Value, owners!, level!.Value) : null;
        }
    }

    /// <summary>Replaces every field, kind included, with the <c>version</c> read with the asset.</summary>
    public sealed record EditAssetBody(
        string? Name, string? Kind, string? PurchasePrice, string? PurchasedOn, string? Note, decimal? WeightGrams, decimal? Fineness,
        string? RegisteredOn, int? MileageKm, string? MileageOn, int? FiscalHp, string? Energy, decimal? DepreciationRate, uint? Version)
    {
        internal EditAssetCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var details = AssetBodies.Details(reader, Kind, PurchasePrice, PurchasedOn, Note, WeightGrams, Fineness,
                RegisteredOn, MileageKm, MileageOn, FiscalHp, Energy, DepreciationRate);
            if (Version is null)
                reader.Add("version", "request.required", "The version read with the asset is required.");
            return reader.Ok ? new EditAssetCommand(id, Name ?? "", details, Version!.Value) : null;
        }
    }

    public sealed record AssetValuationBody(string? AsOf, string? Amount, string? Note);

    /// <summary>
    /// #649: <c>{ occurredOn, amount, category, note?, externalRef?, documentId? }</c>: an expense of a vehicle, <c>category</c> one of
    /// service, repair, tyres, other; <c>externalRef</c> (<c>bank:{lineId}</c>) is unique among the asset's costs; <c>documentId</c> is its invoice (a correction keeps the stored one unless it gives another, or <c>clearDocument</c> is true).
    /// </summary>
    public sealed record AssetCostBody(string? OccurredOn, string? Amount, string? Category, string? Note, string? ExternalRef = null, Guid? DocumentId = null, bool? ClearDocument = null)
    {
        internal AssetCostDraft? Read(FieldReader reader)
        {
            var occurredOn = reader.Date(OccurredOn, "occurredOn");
            var amount = LoanBodies.Number(reader, Amount, "amount");
            return occurredOn is null || amount is null ? null : new AssetCostDraft(occurredOn.Value, amount.Value, Category, Note, ExternalRef, DocumentId, ClearDocument ?? false);
        }
    }

    private static class AssetBodies
    {
        public static AssetDetails Details(
            FieldReader reader, string? kind, string? price, string? purchasedOn, string? note, decimal? weight, decimal? fineness,
            string? registeredOn, int? mileageKm, string? mileageOn, int? fiscalHp, string? energy, decimal? depreciationRate)
            => new(kind ?? "", LoanBodies.Number(reader, price, "purchasePrice", required: false), reader.Date(purchasedOn, "purchasedOn", required: false),
                note, weight, fineness, reader.Date(registeredOn, "registeredOn", required: false), mileageKm,
                reader.Date(mileageOn, "mileageOn", required: false), fiscalHp, energy, depreciationRate);
    }
}
