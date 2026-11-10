using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

public sealed record FlowDraft(LocalDate OccurredOn, string Kind, Money Amount, string? Category, string? ExternalRef, string? Note)
{
    internal Result<NewFlow> ToNewFlow(ItemId item)
        => EnvelopeWire.TryFlowKind(Kind, out var kind)
            ? Result.Success(new NewFlow(item, OccurredOn, kind, Amount, Category, ExternalRef, Note))
            : Result.Failure<NewFlow>(new Failure(CoreFactCodes.InvalidFact, $"'{Kind}' is not a flow kind.",
                new Dictionary<string, object?> { ["field"] = "kind" }));
}

public sealed record RecordValuationCommand(ItemId ItemId, LocalDate AsOf, Money Amount, string? Note) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record VoidValuationCommand(ItemId ItemId, Guid ValuationId) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record RecordFlowCommand(ItemId ItemId, FlowDraft Flow) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// Several flows of one envelope as one write (a statement's movements): what <see cref="RecordFlowCommand"/> checks, made once
/// for all, then <see cref="IFlows.RecordManyAsync"/>. A refusal that names a flow carries its index (<see cref="IFlows.RecordManyIndex"/>).
/// </summary>
public sealed record RecordFlowsCommand(ItemId ItemId, IReadOnlyList<FlowDraft> Flows) : ICommand<IReadOnlyList<Guid>>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record CorrectFlowCommand(ItemId ItemId, Guid FlowId, FlowDraft Flow) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record VoidFlowCommand(ItemId ItemId, Guid FlowId) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>What every fact command checks before calling the Core: an open envelope.</summary>
internal static class OpenEnvelope
{
    public static async Task<Result<Envelope>> RequireAsync(IEnvelopeStore envelopes, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Envelope>(failure!);
        loaded.TryGetValue(out var envelope);
        return EnvelopeCheck.RequireOpen(envelope.Item).TryGetFailure(out failure)
            ? Result.Failure<Envelope>(failure!) : Result.Success(envelope.Envelope);
    }

    /// <summary>Spec C2: an income category only on a withdrawal or income of an envelope that is not a savings account.</summary>
    public static Failure? RefuseIncomeCategory(Envelope envelope, NewFlow flow)
        => FlowCategories.Refusal(envelope.Kind, flow.Kind, flow.Category) is { } message
            ? new Failure(InvestmentsCodes.InvalidIncomeCategory, message, new Dictionary<string, object?> { ["field"] = "category" })
            : null;

    /// <summary>Issue #399: a deposit beyond a livret's ceiling, against its balance without the flow it corrects (<paramref name="replaced"/>).</summary>
    public static async Task<Failure?> AboveCeilingAsync(EnvelopePerformance performance, NewFlow flow, Guid? replaced, CancellationToken ct)
    {
        if (flow.Kind is not (FlowKind.Opening or FlowKind.Contribution)) return null;
        var facts = await performance.FactsAsync(flow.Item, ct);
        if (facts.TryGetFailure(out var failure)) return failure;
        facts.TryGetValue(out var f);
        return EnvelopeInsights.AboveCeiling(f! with { Flows = f.Flows.Where(x => x.Id != replaced).ToList() }, flow.OccurredOn, flow.Amount.Amount, "amount");
    }
}

[RequestHandler<RecordValuationCommand, Guid>]
public sealed class RecordValuationHandler(IEnvelopeStore envelopes, IItemRegistry items, IValuations valuations)
    : IRequestHandler<RecordValuationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordValuationCommand command, CancellationToken ct = default)
    {
        if ((await OpenEnvelope.RequireAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var recorded = await valuations.RecordAsync(new NewValuation(command.ItemId, command.AsOf, command.Amount, command.Note), ct);
        return recorded.TryGetValue(out var valuation) ? Result.Success(valuation!.Id)
            : Result.Failure<Guid>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<VoidValuationCommand, Guid>]
public sealed class VoidValuationHandler(IEnvelopeStore envelopes, IItemRegistry items, IValuations valuations)
    : IRequestHandler<VoidValuationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(VoidValuationCommand command, CancellationToken ct = default)
    {
        if ((await OpenEnvelope.RequireAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var voided = await valuations.VoidAsync(command.ItemId, command.ValuationId, ct);
        return voided.TryGetFailure(out failure) ? Result.Failure<Guid>(failure!) : Result.Success(command.ValuationId);
    }
}

[RequestHandler<RecordFlowCommand, Guid>]
public sealed class RecordFlowHandler(IEnvelopeStore envelopes, IItemRegistry items, IFlows flows, EnvelopePerformance performance)
    : IRequestHandler<RecordFlowCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordFlowCommand command, CancellationToken ct = default)
    {
        Failure? failure = null;
        var open = await OpenEnvelope.RequireAsync(envelopes, items, command.ItemId, ct);
        if (!open.TryGetValue(out var envelope))
            return Result.Failure<Guid>(open.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        var draft = command.Flow.ToNewFlow(command.ItemId);
        if (!draft.TryGetValue(out var flow))
            return Result.Failure<Guid>(draft.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        if (OpenEnvelope.RefuseIncomeCategory(envelope!, flow!) is { } refused)
            return Result.Failure<Guid>(refused);
        if ((await OpenEnvelope.AboveCeilingAsync(performance, flow!, null, ct)) is { } above)
            return Result.Failure<Guid>(above);
        var recorded = await flows.RecordAsync(flow!, ct);
        return recorded.TryGetValue(out var saved) ? Result.Success(saved!.Id)
            : Result.Failure<Guid>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<RecordFlowsCommand, IReadOnlyList<Guid>>]
public sealed class RecordFlowsHandler(IEnvelopeStore envelopes, IItemRegistry items, IFlows flows, EnvelopePerformance performance)
    : IRequestHandler<RecordFlowsCommand, IReadOnlyList<Guid>>
{
    public async ValueTask<Result<IReadOnlyList<Guid>>> HandleAsync(RecordFlowsCommand command, CancellationToken ct = default)
    {
        Failure? failure = null;
        var open = await OpenEnvelope.RequireAsync(envelopes, items, command.ItemId, ct);
        if (!open.TryGetValue(out var envelope))
            return Result.Failure<IReadOnlyList<Guid>>(open.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());

        // A livret's deposits are held against its ceiling, each with the ones before it, as recorded one by one: its facts read once.
        EnvelopePerformance.Facts? facts = null;
        var drafted = new List<NewFlow>(command.Flows.Count);
        for (var i = 0; i < command.Flows.Count; i++)
        {
            var draft = command.Flows[i].ToNewFlow(command.ItemId);
            if (!draft.TryGetValue(out var flow))
                return Refused(draft.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException(), i);
            if (OpenEnvelope.RefuseIncomeCategory(envelope!, flow!) is { } refused)
                return Refused(refused, i);
            if (envelope!.Regulated is not null && flow!.Kind is FlowKind.Opening or FlowKind.Contribution)
            {
                if (facts is null)
                {
                    var read = await performance.FactsAsync(command.ItemId, ct);
                    if (!read.TryGetValue(out facts))
                        return Result.Failure<IReadOnlyList<Guid>>(read.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
                }
                var before = facts! with { Flows = [.. facts.Flows, .. drafted.Select(Pending)] };
                if (EnvelopeInsights.AboveCeiling(before, flow.OccurredOn, flow.Amount.Amount, "amount") is { } above)
                    return Refused(above, i);
            }
            drafted.Add(flow!);
        }

        var recorded = await flows.RecordManyAsync(command.ItemId, drafted, ct);
        return recorded.TryGetValue(out var saved) ? Result.Success<IReadOnlyList<Guid>>(saved!.Select(f => f.Id).ToList())
            : Result.Failure<IReadOnlyList<Guid>>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }

    /// <summary>A flow not saved yet, as the ceiling reads it.</summary>
    private static Flow Pending(NewFlow f) => new(Guid.Empty, f.Item, f.OccurredOn, f.Kind, f.Amount, f.Category, f.ExternalRef, f.Note);

    private static Result<IReadOnlyList<Guid>> Refused(Failure failure, int index) => Result.Failure<IReadOnlyList<Guid>>(Batch.At(failure, index));
}

/// <summary>The commands that write several lines at once (a statement's): a refusal names its line by index.</summary>
public static class Batch
{
    /// <summary>The metadata key of the refused line's index, the same as the Core's (<see cref="IFlows.RecordManyIndex"/>).</summary>
    public const string Index = IFlows.RecordManyIndex;

    /// <summary>A rule's refusal, with the index of its line; a typed failure (not found, forbidden) is no line's and stays as it is.</summary>
    public static Failure At(Failure failure, int index)
    {
        if (failure.GetType() != typeof(Failure))
            return failure;
        var metadata = failure.Metadata is { } m ? new Dictionary<string, object?>(m) : [];
        metadata[Index] = index;
        return new Failure(failure.Code, failure.Message, metadata);
    }

    /// <summary>The index a refusal names, if any.</summary>
    public static int? IndexOf(Failure failure)
        => failure.Metadata is { } m && m.TryGetValue(Index, out var named) && named is int index ? index : null;
}

[RequestHandler<CorrectFlowCommand, Guid>]
public sealed class CorrectFlowHandler(IEnvelopeStore envelopes, IItemRegistry items, IFlows flows, EnvelopePerformance performance)
    : IRequestHandler<CorrectFlowCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CorrectFlowCommand command, CancellationToken ct = default)
    {
        Failure? failure = null;
        var open = await OpenEnvelope.RequireAsync(envelopes, items, command.ItemId, ct);
        if (!open.TryGetValue(out var envelope))
            return Result.Failure<Guid>(open.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        var draft = command.Flow.ToNewFlow(command.ItemId);
        if (!draft.TryGetValue(out var flow))
            return Result.Failure<Guid>(draft.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        // Spec C2b: a transfer leg is corrected through its transfer; checked first, it is the more specific refusal.
        var replaced = await flows.FindManyAsync([command.FlowId], ct);
        if (replaced.Any(f => f.Item == command.ItemId && f.Category == FlowCategories.Transfer) && flow!.Category != FlowCategories.Transfer)
            return Result.Failure<Guid>(new Failure(InvestmentsCodes.TransferLeg, "A transfer leg is corrected through its transfer.",
                new Dictionary<string, object?> { ["field"] = "category" }));
        if (OpenEnvelope.RefuseIncomeCategory(envelope!, flow!) is { } refused)
            return Result.Failure<Guid>(refused);
        if ((await OpenEnvelope.AboveCeilingAsync(performance, flow!, command.FlowId, ct)) is { } above)
            return Result.Failure<Guid>(above);
        var corrected = await flows.SupersedeAsync(command.ItemId, command.FlowId, flow!, ct);
        return corrected.TryGetValue(out var saved) ? Result.Success(saved!.Id)
            : Result.Failure<Guid>(corrected.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<VoidFlowCommand, Guid>]
public sealed class VoidFlowHandler(IEnvelopeStore envelopes, IItemRegistry items, IFlows flows)
    : IRequestHandler<VoidFlowCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(VoidFlowCommand command, CancellationToken ct = default)
    {
        if ((await OpenEnvelope.RequireAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var voided = await flows.VoidAsync(command.ItemId, command.FlowId, ct);
        return voided.TryGetFailure(out failure) ? Result.Failure<Guid>(failure!) : Result.Success(command.FlowId);
    }
}
