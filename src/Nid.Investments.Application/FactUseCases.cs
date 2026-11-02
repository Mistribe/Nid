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
public sealed class RecordFlowHandler(IEnvelopeStore envelopes, IItemRegistry items, IFlows flows)
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
        var recorded = await flows.RecordAsync(flow!, ct);
        return recorded.TryGetValue(out var saved) ? Result.Success(saved!.Id)
            : Result.Failure<Guid>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<CorrectFlowCommand, Guid>]
public sealed class CorrectFlowHandler(IEnvelopeStore envelopes, IItemRegistry items, IFlows flows)
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
