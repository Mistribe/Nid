using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>2E2 spec §4: the month's envelopes for the routine, as of <c>On</c> (default: the end of the previous month).</summary>
public sealed record GetRoutineQuery(LocalDate? On) : IQuery<RoutineView>;

/// <summary>
/// Issue #34 §2.6: with a units-of-account floor, <c>UcSharePct</c> is the share in the latest holdings snapshot on or before the date (null
/// without one) and <c>UcShareBelowFloor</c> says it is under the floor.
/// </summary>
public sealed record RoutineLineView(
    ItemId Id, string Name, string Currency, string Cadence, AmountView? LastValue, LocalDate? LastAsOf, bool Due,
    decimal? UcShareFloorPct, decimal? UcSharePct, bool UcShareBelowFloor, AmountView? CeilingRoom = null);

public sealed record RoutineView(LocalDate On, IReadOnlyList<RoutineLineView> Lines) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Lines.Select(l => l.Id);
}

internal static class Routine
{
    public static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public static Failure Field(string code, string message, string field)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });
}

[RequestHandler<GetRoutineQuery, RoutineView>]
public sealed class GetRoutineHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IAccessPolicy policy, IActorAccessor actor,
    IHouseholdCalendar calendar, IHoldingStore holdings)
    : IRequestHandler<GetRoutineQuery, RoutineView>
{
    public async ValueTask<Result<RoutineView>> HandleAsync(GetRoutineQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var on = query.On ?? RoutineMath.DefaultDate(today);
        if (on > today)
            return Result.Failure<RoutineView>(Routine.Field(CoreFactCodes.DateInFuture, "The routine's date cannot be after today.", "on"));

        var lines = new List<RoutineLineView>();
        foreach (var envelope in await envelopes.FindManyAsync((await visible.ForAsync(reader, VisibilityScope.Me, ct)).ToList(), ct))
        {
            if (!(await policy.CheckAsync(reader, envelope.ItemId, AccessKind.Write, ct)).IsSuccess) continue;
            if (!(await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out var f) || f!.Item.ArchivedAt is not null) continue;
            var last = f.Values.Where(v => v.AsOf <= on).OrderByDescending(v => v.AsOf).FirstOrDefault();
            var share = envelope.UcShareFloorPct is null ? null : await UcShareAsync(envelope.ItemId, on, ct);
            lines.Add(new RoutineLineView(envelope.ItemId, f.Item.DisplayName, f.Currency.Code, EnvelopeWire.Of(envelope.Cadence),
                last is null ? null : AmountView.From(last.Value), last?.AsOf,
                RoutineMath.IsDue(envelope.Cadence, f.Values.Select(v => v.AsOf), on), envelope.UcShareFloorPct, share, share < envelope.UcShareFloorPct,
                EnvelopeInsights.Ceiling(f, on)?.Room));
        }
        return Result.Success(new RoutineView(on, lines
            .OrderBy(l => l.Due ? 0 : 1).ThenBy(l => l.Name, Routine.French).ThenBy(l => l.Id.Value).ToList()));
    }

    private async Task<decimal?> UcShareAsync(ItemId envelope, LocalDate on, CancellationToken ct)
        => (await holdings.SnapshotsAsync(envelope, ct)).FirstOrDefault(s => s.AsOf <= on) is { } snapshot
            ? HoldingMath.UcSharePct(snapshot, (await holdings.HoldingsAsync(envelope, ct)).ToDictionary(h => h.Holding.Id, h => h.Holding.Kind))
            : null;
}

public sealed record RoutineEntry(ItemId Envelope, Money? Value, Money? Contribution);

/// <summary>2E2 spec §4: every line in one transaction, all or nothing; answers the recap.</summary>
public sealed record RecordRoutineCommand(LocalDate On, IReadOnlyList<RoutineEntry> Lines) : ICommand<RoutineRecap>;

public sealed record RoutineRecapLine(
    ItemId Envelope, string Name, AmountView? PreviousValue, LocalDate? PreviousAsOf, AmountView? Value, AmountView Contributions,
    AmountView? Gain, Guid? ValuationId, Guid? FlowId);

public sealed record RoutineRecap(LocalDate On, IReadOnlyList<RoutineRecapLine> Lines) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Lines.Select(l => l.Envelope);
}

public sealed record RoutineFact(ItemId Envelope, Guid? ValuationId, Guid? FlowId);

/// <summary>2E2 spec §4: voids what a routine recorded, in one transaction (history kept).</summary>
public sealed record UndoRoutineCommand(IReadOnlyList<RoutineFact> Facts) : ICommand<RoutineUndone>;

public sealed record RoutineUndone(int Voided);

[RequestHandler<RecordRoutineCommand, RoutineRecap>]
public sealed class RecordRoutineHandler(
    EnvelopePerformance performance, IValuations valuations, IFlows flows,
    IAccessPolicy policy, IActorAccessor actor, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordRoutineCommand, RoutineRecap>
{
    public async ValueTask<Result<RoutineRecap>> HandleAsync(RecordRoutineCommand command, CancellationToken ct = default)
    {
        if (command.Lines.Count == 0)
            return Fail(InvestmentsCodes.RoutineEmptyLine, "A routine records at least one line.", "lines");
        if (command.On > await calendar.TodayAsync(ct))
            return Fail(CoreFactCodes.DateInFuture, "The routine's date cannot be after today.", "on");

        var seen = new HashSet<ItemId>();
        for (var i = 0; i < command.Lines.Count; i++)
        {
            var line = command.Lines[i];
            if (line.Value is null && line.Contribution is null)
                return Fail(InvestmentsCodes.RoutineEmptyLine, "A line has a value or a contribution.", $"lines[{i}]");
            if (!seen.Add(line.Envelope))
                return Fail(InvestmentsCodes.RoutineDuplicate, "An envelope appears once in a routine.", $"lines[{i}]");
            if (line.Value is { Amount: < 0m })
                return Fail(InvestmentsCodes.RoutineInvalidAmount, "A value cannot be negative.", $"lines[{i}].value");
            if (line.Contribution is { Amount: <= 0m })
                return Fail(InvestmentsCodes.RoutineInvalidAmount, "A contribution is positive.", $"lines[{i}].contribution");
        }

        // Access and facts before any write: one refusal leaves nothing written (never a 403 that would disclose an envelope).
        var reader = actor.Current!.PersonId!.Value;
        var before = new List<(RoutineEntry Line, string Name, Currency Currency, Valuation? Previous, IReadOnlyList<Flow> Flows)>();
        for (var i = 0; i < command.Lines.Count; i++)
        {
            var line = command.Lines[i];
            if (!(await policy.CheckAsync(reader, line.Envelope, AccessKind.Write, ct)).IsSuccess)
                return Result.Failure<RoutineRecap>(new NotFoundFailure("envelope", line.Envelope.ToString()));
            var facts = await performance.FactsAsync(line.Envelope, ct);
            if (facts.TryGetFailure(out var failure)) return Result.Failure<RoutineRecap>(failure!);
            facts.TryGetValue(out var f);
            if (f!.Item.ArchivedAt is not null)
                return Result.Failure<RoutineRecap>(new NotFoundFailure("envelope", line.Envelope.ToString()));
            foreach (var (money, field) in new[] { (line.Value, "value"), (line.Contribution, "contribution") })
                if (money is { } m && m.Currency != f.Currency)
                    return Fail(CoreFactCodes.CurrencyMismatch, "Amounts are in the envelope's currency.", $"lines[{i}].{field}");
            if (line.Contribution is { } paid && EnvelopeInsights.AboveCeiling(f, command.On, paid.Amount, $"lines[{i}].contribution") is { } above)
                return Result.Failure<RoutineRecap>(above);
            if (line.Value is not null && f.Values.Any(v => v.AsOf == command.On))
                return Fail(InvestmentsCodes.RoutineValueExists, "This envelope already has a value at this date: correct it from its page.", $"lines[{i}].value");
            var previous = f.Values.Where(v => v.AsOf <= command.On).OrderByDescending(v => v.AsOf).FirstOrDefault();
            before.Add((line, f.Item.DisplayName, f.Currency, previous, f.Flows));
        }

        var written = new List<(Guid? Valuation, Guid? Flow)>();
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            foreach (var (line, _, _, _, _) in before)
            {
                Guid? valuationId = null, flowId = null;
                if (line.Value is { } value)
                {
                    var recorded = await valuations.RecordAsync(new NewValuation(line.Envelope, command.On, value, null), ct);
                    if (recorded.TryGetFailure(out var failure)) return Result.Failure<RoutineRecap>(failure!);
                    recorded.TryGetValue(out var v);
                    valuationId = v!.Id;
                }
                if (line.Contribution is { } contribution)
                {
                    var recorded = await flows.RecordAsync(new NewFlow(line.Envelope, command.On, FlowKind.Contribution, contribution, "routine", null, null), ct);
                    if (recorded.TryGetFailure(out var failure)) return Result.Failure<RoutineRecap>(failure!);
                    recorded.TryGetValue(out var fl);
                    flowId = fl!.Id;
                }
                written.Add((valuationId, flowId));
            }
            await transaction.CommitAsync(ct);
        }

        var recap = before.Select((b, i) =>
        {
            var contributions = RoutineMath.NetContributions(b.Flows, b.Previous?.AsOf, command.On) + (b.Line.Contribution?.Amount ?? 0m);
            decimal? gain = b.Previous is { } p && b.Line.Value is { } v ? v.Amount - p.Value.Amount - contributions : null;
            return new RoutineRecapLine(b.Line.Envelope, b.Name, AmountView.From(b.Previous?.Value), b.Previous?.AsOf, AmountView.From(b.Line.Value),
                AmountView.From(ToMoney(contributions, b.Currency)), gain is { } g ? AmountView.From(ToMoney(g, b.Currency)) : null,
                written[i].Valuation, written[i].Flow);
        }).ToList();
        return Result.Success(new RoutineRecap(command.On, recap));
    }

    private static Money ToMoney(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var m) ? m : throw new InvalidOperationException();

    private static Result<RoutineRecap> Fail(string code, string message, string field) => Result.Failure<RoutineRecap>(Routine.Field(code, message, field));
}

[RequestHandler<UndoRoutineCommand, RoutineUndone>]
public sealed class UndoRoutineHandler(IEnvelopeStore envelopes, IItemRegistry items, IValuations valuations, IFlows flows, IAccessPolicy policy, IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<UndoRoutineCommand, RoutineUndone>
{
    public async ValueTask<Result<RoutineUndone>> HandleAsync(UndoRoutineCommand command, CancellationToken ct = default)
    {
        if (command.Facts.Count == 0)
            return Result.Failure<RoutineUndone>(Routine.Field(InvestmentsCodes.RoutineEmptyLine, "Nothing to undo.", "facts"));
        var reader = actor.Current!.PersonId!.Value;
        foreach (var fact in command.Facts)
            if (!(await policy.CheckAsync(reader, fact.Envelope, AccessKind.Write, ct)).IsSuccess)
                return Result.Failure<RoutineUndone>(new NotFoundFailure("envelope", fact.Envelope.ToString()));
        // Only an open envelope's facts: not another item's (an income source's flows), not a closed envelope's.
        foreach (var fact in command.Facts)
        {
            var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, fact.Envelope, ct);
            if (loaded.TryGetFailure(out var failure)) return Result.Failure<RoutineUndone>(failure!);
            loaded.TryGetValue(out var l);
            if (l.Item.ArchivedAt is not null)
                return Result.Failure<RoutineUndone>(new NotFoundFailure("envelope", fact.Envelope.ToString()));
        }

        var voided = 0;
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            foreach (var fact in command.Facts)
            {
                if (fact.ValuationId is { } v)
                {
                    var result = await valuations.VoidAsync(fact.Envelope, v, ct);
                    if (result.TryGetFailure(out var failure)) return Result.Failure<RoutineUndone>(failure!);
                    voided++;
                }
                if (fact.FlowId is { } f)
                {
                    var result = await flows.VoidAsync(fact.Envelope, f, ct);
                    if (result.TryGetFailure(out var failure)) return Result.Failure<RoutineUndone>(failure!);
                    voided++;
                }
            }
            await transaction.CommitAsync(ct);
        }
        return Result.Success(new RoutineUndone(voided));
    }
}
