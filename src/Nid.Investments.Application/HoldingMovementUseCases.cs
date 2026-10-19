using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>A movement as a body sends it (issue #34 §2.3); checked by <see cref="MovementRules"/>. Units are signed.</summary>
public sealed record MovementDraft(
    LocalDate OccurredOn, string? Kind, decimal? Units, decimal? UnitPrice, Money Amount, Guid? EnvelopeFlowId, string? Label, string? ExternalRef);

public sealed record SwitchLegDraft(Guid HoldingId, Money Amount, decimal? Units, decimal? UnitPrice);

/// <summary>A switch between two holdings: <c>switch_out</c> (or <c>maturity</c>) on <see cref="From"/>, <c>switch_in</c> on <see cref="To"/>.</summary>
public sealed record SwitchDraft(LocalDate OccurredOn, bool Maturity, SwitchLegDraft From, SwitchLegDraft To, string? Label);

public sealed record AddHoldingMovementCommand(ItemId ItemId, Guid Holding, MovementDraft Movement) : ICommand<MovementRecorded>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Both legs of a switch in one write, sharing a generated <c>switch_ref</c>.</summary>
public sealed record RecordSwitchCommand(ItemId ItemId, SwitchDraft Switch) : ICommand<SwitchRecorded>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ListHoldingMovementsQuery(ItemId ItemId, Guid Holding) : IQuery<HoldingMovementList>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record GetHoldingSeriesQuery(ItemId ItemId, Guid Holding) : IQuery<HoldingSeries>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record GetHoldingPerformanceQuery(ItemId ItemId, Guid Holding, LocalDate From, LocalDate To) : IQuery<PeriodView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Issue #34 §2.3, checked before anything is written; <c>at</c> prefixes the fields of a switch's leg.</summary>
internal static class MovementRules
{
    public const int TextMax = 200;

    public static Result<HoldingMovement> Check(
        ItemId envelope, Guid holdingId, string? kind, LocalDate on, decimal? units, decimal? price, Money amount, Guid? flowId, string? switchRef,
        string? label, string? externalRef, IReadOnlyList<StoredHolding> holdings, IReadOnlyList<Flow> flows, Currency currency, LocalDate today, string at = "")
    {
        var holding = holdings.FirstOrDefault(h => h.Holding.Id == holdingId)?.Holding;
        if (holding is null)
            return Invalid($"{at}holdingId", "This holding is not one of this envelope's.");
        if (kind is null || !MovementKinds.All.Contains(kind))
            return Invalid("kind", $"'{kind}' is not a movement kind.");
        if (on > today)
            return Invalid("occurredOn", "A movement is dated today or earlier.");
        if (holding.ClosedOn is { } closed && closed < on)
            return Result.Failure<HoldingMovement>(new Failure(InvestmentsCodes.HoldingClosed, "This holding was closed before the movement's date.",
                new Dictionary<string, object?> { ["field"] = $"{at}holdingId" }));
        if (amount.Currency != currency)
            return Invalid($"{at}amount", "A movement is in the envelope's currency.");
        if (amount.Amount <= 0)
            return Invalid($"{at}amount", "An amount is above zero; the kind gives its direction.");
        if (!HoldingWire.HasUnits(holding.Kind) && (units is not null || price is not null))
            return Invalid($"{at}units", "A euro fund or cash movement has no units and no unit price.");
        if (units is { } u && (u == 0 || Math.Round(u, 6) != u || u > 0 != MovementKinds.IsIn(kind)))
            return Invalid($"{at}units", "Units are signed by the kind (bought +, sold or deducted −), not zero, with 6 decimals at most.");
        if (price is { } p && (p < 0 || Math.Round(p, 6) != p))
            return Invalid($"{at}unitPrice", "Zero or more, with 6 decimals at most.");
        if (flowId is { } f && flows.All(x => x.Id != f))
            return Invalid("envelopeFlowId", "This flow is not one of this envelope's.");
        if (label is { Length: > TextMax })
            return Invalid("label", $"A label has at most {TextMax} characters.");
        if (externalRef is { Length: > TextMax })
            return Invalid("externalRef", $"A reference has at most {TextMax} characters.");
        return Result.Success(new HoldingMovement(Guid.CreateVersion7(), envelope, holdingId, on, kind, units, price, amount.Amount, flowId, switchRef,
            label, externalRef));
    }

    private static Result<HoldingMovement> Invalid(string field, string message)
        => Result.Failure<HoldingMovement>(new Failure(InvestmentsCodes.InvalidMovement, message, new Dictionary<string, object?> { ["field"] = field }));
}

/// <summary>What every movement write and read loads: the envelope, its holdings, flows and movements.</summary>
internal sealed record MovementFacts(Currency Currency, IReadOnlyList<StoredHolding> Holdings, IReadOnlyList<Flow> Flows, IReadOnlyList<HoldingMovement> Movements)
{
    public static async Task<Result<MovementFacts>> LoadAsync(
        IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows, ItemId id, bool write, Guid? holding, CancellationToken ct)
    {
        var envelope = write ? await HoldingCheck.OpenAsync(envelopes, items, id, ct) : await HoldingCheck.ReadableAsync(envelopes, items, id, ct);
        if (!envelope.TryGetValue(out var found))
            return Result.Failure<MovementFacts>(envelope.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var listed = await flows.ListAsync(id, EnvelopeCheck.Everything, null, ct);
        if (!listed.TryGetValue(out var flowList))
            return Result.Failure<MovementFacts>(listed.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var all = await holdings.HoldingsAsync(id, ct);
        if (holding is { } h && all.All(x => x.Holding.Id != h))
            return Result.Failure<MovementFacts>(new NotFoundFailure("holding", h.ToString())); // spec D3: another envelope's holding is absent
        return Result.Success(new MovementFacts(found.Currency, all, flowList!, await holdings.MovementsAsync(id, ct)));
    }

    /// <summary>The movement warnings on the dates just written, once they are recorded.</summary>
    public IReadOnlyList<HoldingWarningView> WarningsAfter(IReadOnlyList<HoldingMovement> written)
        => HoldingMath.Warnings(Movements.Concat(written).ToList(), Flows)
            .Where(w => written.Any(m => m.OccurredOn == w.On)).Select(HoldingWarningView.Of).ToList();
}

[RequestHandler<AddHoldingMovementCommand, MovementRecorded>]
public sealed class AddHoldingMovementHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<AddHoldingMovementCommand, MovementRecorded>
{
    public async ValueTask<Result<MovementRecorded>> HandleAsync(AddHoldingMovementCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await MovementFacts.LoadAsync(envelopes, items, holdings, flows, command.ItemId, write: true, command.Holding, ct);
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<MovementRecorded>(loaded.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        var d = command.Movement;
        if (d.Kind is MovementKinds.SwitchIn or MovementKinds.SwitchOut)
            return Result.Failure<MovementRecorded>(new Failure(InvestmentsCodes.InvalidMovement, "A switch is recorded with both its legs, on /switches.",
                new Dictionary<string, object?> { ["field"] = "kind" }));
        var checkedMovement = MovementRules.Check(command.ItemId, command.Holding, d.Kind, d.OccurredOn, d.Units, d.UnitPrice, d.Amount, d.EnvelopeFlowId, null,
            d.Label, d.ExternalRef, facts!.Holdings, facts.Flows, facts.Currency, await calendar.TodayAsync(ct));
        if (!checkedMovement.TryGetValue(out var movement))
            return Result.Failure<MovementRecorded>(checkedMovement.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        await holdings.AddMovementsAsync([movement], ct);
        await transaction.CommitAsync(ct);
        return Result.Success(new MovementRecorded(movement.Id, facts.WarningsAfter([movement])));
    }
}

[RequestHandler<RecordSwitchCommand, SwitchRecorded>]
public sealed class RecordSwitchHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordSwitchCommand, SwitchRecorded>
{
    public async ValueTask<Result<SwitchRecorded>> HandleAsync(RecordSwitchCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await MovementFacts.LoadAsync(envelopes, items, holdings, flows, command.ItemId, write: true, null, ct);
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<SwitchRecorded>(loaded.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        var s = command.Switch;
        if (s.From.HoldingId == s.To.HoldingId)
            return Result.Failure<SwitchRecorded>(new Failure(InvestmentsCodes.InvalidMovement, "A switch moves money between two holdings.",
                new Dictionary<string, object?> { ["field"] = "to.holdingId" }));
        var today = await calendar.TodayAsync(ct);
        var reference = Guid.CreateVersion7().ToString("N");
        var legs = new[]
        {
            (s.From, s.Maturity ? MovementKinds.Maturity : MovementKinds.SwitchOut, "from."),
            (s.To, MovementKinds.SwitchIn, "to."),
        }.Select(l => MovementRules.Check(command.ItemId, l.Item1.HoldingId, l.Item2, s.OccurredOn, l.Item1.Units, l.Item1.UnitPrice, l.Item1.Amount, null,
            reference, s.Label, null, facts!.Holdings, facts.Flows, facts.Currency, today, l.Item3)).ToList();
        var written = new List<HoldingMovement>();
        foreach (var leg in legs)
        {
            if (!leg.TryGetValue(out var movement))
                return Result.Failure<SwitchRecorded>(leg.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
            written.Add(movement);
        }

        await holdings.AddMovementsAsync(written, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(new SwitchRecorded(reference, written[0].Id, written[1].Id, facts!.WarningsAfter(written)));
    }
}

[RequestHandler<ListHoldingMovementsQuery, HoldingMovementList>]
public sealed class ListHoldingMovementsHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows)
    : IRequestHandler<ListHoldingMovementsQuery, HoldingMovementList>
{
    public async ValueTask<Result<HoldingMovementList>> HandleAsync(ListHoldingMovementsQuery query, CancellationToken ct = default)
    {
        var loaded = (await MovementFacts.LoadAsync(envelopes, items, holdings, flows, query.ItemId, write: false, query.Holding, ct));
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<HoldingMovementList>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        return Result.Success(new HoldingMovementList(query.ItemId, query.Holding,
            facts!.Movements.Where(m => m.HoldingId == query.Holding).Select(m => MovementView.Of(m, facts.Currency)).ToList()));
    }
}

[RequestHandler<GetHoldingSeriesQuery, HoldingSeries>]
public sealed class GetHoldingSeriesHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows)
    : IRequestHandler<GetHoldingSeriesQuery, HoldingSeries>
{
    public async ValueTask<Result<HoldingSeries>> HandleAsync(GetHoldingSeriesQuery query, CancellationToken ct = default)
    {
        var loaded = (await MovementFacts.LoadAsync(envelopes, items, holdings, flows, query.ItemId, write: false, query.Holding, ct));
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<HoldingSeries>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var points = HoldingMath.Series(query.Holding, await holdings.SnapshotsAsync(query.ItemId, ct), facts!.Movements, facts.Currency)
            .Select(p => new SeriesPoint(p.AsOf, AmountView.From(p.Value), AmountView.From(p.NetContributions)))
            .ToList();
        return Result.Success(new HoldingSeries(query.ItemId, query.Holding, points));
    }
}

[RequestHandler<GetHoldingPerformanceQuery, PeriodView>]
public sealed class GetHoldingPerformanceHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows)
    : IRequestHandler<GetHoldingPerformanceQuery, PeriodView>
{
    public async ValueTask<Result<PeriodView>> HandleAsync(GetHoldingPerformanceQuery query, CancellationToken ct = default)
    {
        if (query.From > query.To)
            return Result.Failure<PeriodView>(new Failure(InvestmentsCodes.InvalidPeriod, "The period's start is after its end.", null));
        var loaded = (await MovementFacts.LoadAsync(envelopes, items, holdings, flows, query.ItemId, write: false, query.Holding, ct));
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<PeriodView>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        return Result.Success(PeriodView.Of(HoldingMath.Period(query.Holding, await holdings.SnapshotsAsync(query.ItemId, ct), facts!.Movements,
            facts.Currency, query.From, query.To)));
    }
}
