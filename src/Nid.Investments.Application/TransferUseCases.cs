using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>
/// 2B1a spec §5: <see cref="ItemId"/> is the source; the pipeline checks it, the handler checks <see cref="To"/>.
/// Fix round 1 (controller ruling): the destination is a second item the actor names in the payload, not the
/// route's own — like a purchase's grant target (<c>ItemWriteGuard.EnsureWritableAsync</c>,
/// <c>Nid.Subscriptions.Application/WriteUseCases.cs</c>), its refusal is always "not found", whether it is
/// forbidden (readable, not writable) or hidden (not readable): never a 403 that would disclose it.
/// </summary>
public sealed record TransferCommand(ItemId ItemId, ItemId To, LocalDate OccurredOn, Money Amount, string? Note) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record CloseEnvelopeCommand(ItemId ItemId, LocalDate ClosedOn, Money? FinalWithdrawal) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReopenEnvelopeCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record GetPeriodPerformanceQuery(ItemId ItemId, LocalDate From, LocalDate To) : IQuery<PeriodView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<TransferCommand, Guid>]
public sealed class TransferHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IFlows flows, IAccessPolicy policy, IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<TransferCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(TransferCommand command, CancellationToken ct = default)
    {
        if (command.To == command.ItemId)
            return Fail(InvestmentsCodes.TransferSameEnvelope, "A transfer goes from one envelope to another.");

        // The destination: never disclosed if the actor may not write it — the same "not found", forbidden
        // or hidden alike, that ItemWriteGuard.EnsureWritableAsync gives a purchase's grant target.
        var access = await policy.CheckAsync(actor.Current!.PersonId!.Value, command.To, AccessKind.Write, ct);
        if (access.TryGetFailure(out _))
            return Result.Failure<Guid>(new NotFoundFailure("envelope", command.To.ToString()));

        Failure? failure = null;
        foreach (var id in new[] { command.ItemId, command.To })
            if ((await OpenEnvelope.RequireAsync(envelopes, items, id, ct)).TryGetFailure(out failure))
                return Result.Failure<Guid>(failure!);

        var transfer = Guid.CreateVersion7();
        var reference = $"transfer:{transfer}";
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var withdrawal = await flows.RecordAsync(new NewFlow(command.ItemId, command.OccurredOn, FlowKind.Withdrawal, command.Amount, "transfer", reference, command.Note), ct);
        if (withdrawal.TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);
        var contribution = await flows.RecordAsync(new NewFlow(command.To, command.OccurredOn, FlowKind.Contribution, command.Amount, "transfer", reference, command.Note), ct);
        if (contribution.TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!.Code == CoreFactCodes.CurrencyMismatch
                ? new Failure(InvestmentsCodes.TransferCurrencyMismatch, "Both envelopes of a transfer are in the same currency.", null)
                : failure);
        await transaction.CommitAsync(ct);
        return Result.Success(transfer);
    }

    private static Result<Guid> Fail(string code, string message) => Result.Failure<Guid>(new Failure(code, message, null));
}

[RequestHandler<CloseEnvelopeCommand, ItemId>]
public sealed class CloseEnvelopeHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IValuations valuations, IFlows flows, IUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CloseEnvelopeCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CloseEnvelopeCommand command, CancellationToken ct = default)
    {
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var envelope);
        if (EnvelopeCheck.RequireOpen(envelope.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        var zero = await flows.SumAsync(command.ItemId, new HashSet<FlowKind>(), DateRange.Create(command.ClosedOn, command.ClosedOn.PlusDays(1)).TryGetValue(out var day) ? day : throw new InvalidOperationException(), ct);
        if (!zero.TryGetValue(out var nothing))
            return Result.Failure<ItemId>(zero.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());

        await using var transaction = await unitOfWork.BeginAsync(ct);
        if (command.FinalWithdrawal is { } withdrawal)
        {
            var recorded = await flows.RecordAsync(new NewFlow(command.ItemId, command.ClosedOn, FlowKind.Withdrawal, withdrawal, null, null, "clôture"), ct);
            if (recorded.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }

        var value = await valuations.RecordAsync(new NewValuation(command.ItemId, command.ClosedOn, nothing, "clôture"), ct);
        if (value.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        var archived = await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct);
        if (archived.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<ReopenEnvelopeCommand, ItemId>]
public sealed class ReopenEnvelopeHandler(IEnvelopeStore envelopes, IItemRegistry items)
    : IRequestHandler<ReopenEnvelopeCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReopenEnvelopeCommand command, CancellationToken ct = default)
    {
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var envelope);
        if (envelope.Item.ArchivedAt is null)
            return Result.Failure<ItemId>(new Failure(InvestmentsCodes.EnvelopeOpen, "This envelope is not closed.", null));
        var reopened = await items.UnarchiveAsync(command.ItemId, ct);
        return reopened.TryGetFailure(out failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }
}

[RequestHandler<GetPeriodPerformanceQuery, PeriodView>]
public sealed class GetPeriodPerformanceHandler(IEnvelopePerformance performance)
    : IRequestHandler<GetPeriodPerformanceQuery, PeriodView>
{
    public async ValueTask<Result<PeriodView>> HandleAsync(GetPeriodPerformanceQuery query, CancellationToken ct = default)
    {
        var period = await performance.PeriodAsync(query.ItemId, query.From, query.To, ct);
        return period.TryGetValue(out var p) ? Result.Success(PeriodView.Of(p!))
            : Result.Failure<PeriodView>(period.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
    }
}
