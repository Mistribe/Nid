using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>A holding's breakdown, the latest version of its ISIN (holdings exposure spec C2): <c>recorded_by</c> is never shown.</summary>
public sealed record HoldingBreakdownView(
    LocalDate AsOf, IReadOnlyList<BreakdownPart> Regions, IReadOnlyList<BreakdownPart>? Sectors, bool Stale, Guid? DocumentId) : IExposesDocuments
{
    public IEnumerable<Guid> ExposedDocuments() => DocumentId is { } d ? [d] : [];

    public object WithReadableDocuments(IReadOnlySet<Guid> readable) => this with { DocumentId = readable.Keep(DocumentId) };
}

public sealed record GetHoldingBreakdownQuery(ItemId ItemId, Guid Holding) : IQuery<HoldingBreakdownView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record RecordHoldingBreakdownCommand(
    ItemId ItemId, Guid Holding, LocalDate AsOf, IReadOnlyList<BreakdownPart> Regions, IReadOnlyList<BreakdownPart>? Sectors) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteHoldingBreakdownCommand(ItemId ItemId, Guid Holding, LocalDate AsOf) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<GetHoldingBreakdownQuery, HoldingBreakdownView>]
public sealed class GetHoldingBreakdownHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, ISecurityBreakdownStore breakdowns, IHouseholdCalendar calendar)
    : IRequestHandler<GetHoldingBreakdownQuery, HoldingBreakdownView>
{
    public async ValueTask<Result<HoldingBreakdownView>> HandleAsync(GetHoldingBreakdownQuery query, CancellationToken ct = default)
    {
        if ((await HoldingCheck.ReadableAsync(envelopes, items, query.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<HoldingBreakdownView>(failure!);
        var holding = (await holdings.HoldingsAsync(query.ItemId, ct)).FirstOrDefault(h => h.Holding.Id == query.Holding)?.Holding;
        if (holding?.Isin is not { } isin)
            return Result.Failure<HoldingBreakdownView>(new NotFoundFailure("holding", query.Holding.ToString()));
        if (!(await breakdowns.LatestAsync([isin], ct)).TryGetValue(isin, out var b))
            return Result.Failure<HoldingBreakdownView>(new NotFoundFailure("breakdown", isin));
        return Result.Success(new HoldingBreakdownView(
            b.AsOf, b.Regions, b.Sectors, BreakdownRules.IsStale(b.AsOf, holding.Kind, await calendar.TodayAsync(ct)), b.DocumentId));
    }
}

[RequestHandler<RecordHoldingBreakdownCommand, Guid>]
public sealed class RecordHoldingBreakdownHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, ISecurityBreakdownStore breakdowns,
    IHouseholdCalendar calendar, IActorAccessor actor, IClock clock)
    : IRequestHandler<RecordHoldingBreakdownCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordHoldingBreakdownCommand command, CancellationToken ct = default)
    {
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var holding = (await holdings.HoldingsAsync(command.ItemId, ct)).FirstOrDefault(h => h.Holding.Id == command.Holding)?.Holding;
        if (holding?.Isin is not { } isin)
            return Result.Failure<Guid>(new NotFoundFailure("holding", command.Holding.ToString()));
        if (holding.Kind is "euro_fund" or "cash")
            return Result.Failure<Guid>(new Failure(BreakdownCodes.NotMarket, "A euro fund or cash has no market exposure to break down.", null));

        var errors = BreakdownRules.Check(command.Regions, command.Sectors).Select(e => new FieldError(e.Path, e.Code, e.Message)).ToList();
        if (command.AsOf > await calendar.TodayAsync(ct))
            errors.Add(new FieldError("asOf", FieldErrors.ShapeCode, "A breakdown is not dated in the future."));
        if (errors.Count > 0)
            return Result.Failure<Guid>(errors.Any(e => e.Code == BreakdownCodes.Not100)
                ? FieldErrors.Many(BreakdownCodes.Not100, "Some fields are not valid.", errors)
                : FieldErrors.Shape(errors));

        return Result.Success(await breakdowns.WriteAsync(new SecurityBreakdown(
            Guid.CreateVersion7(), isin, command.AsOf, command.Regions, command.Sectors, null, actor.Current!.PersonId!.Value, clock.GetCurrentInstant()), ct));
    }
}

[RequestHandler<DeleteHoldingBreakdownCommand, bool>]
public sealed class DeleteHoldingBreakdownHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, ISecurityBreakdownStore breakdowns)
    : IRequestHandler<DeleteHoldingBreakdownCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteHoldingBreakdownCommand command, CancellationToken ct = default)
    {
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        var isin = (await holdings.HoldingsAsync(command.ItemId, ct)).FirstOrDefault(h => h.Holding.Id == command.Holding)?.Holding.Isin;
        if (isin is null)
            return Result.Failure<bool>(new NotFoundFailure("holding", command.Holding.ToString()));
        return await breakdowns.DeleteAsync(isin, command.AsOf, ct)
            ? Result.Success(true)
            : Result.Failure<bool>(new NotFoundFailure("breakdown", $"{isin}@{command.AsOf}"));
    }
}
