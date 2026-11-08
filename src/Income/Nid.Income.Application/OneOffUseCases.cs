using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Income.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

public sealed record RecordIncomeOneOffCommand(ItemId ItemId, LocalDate OccurredOn, Money Amount, string? Note, Money? TaxableAmount = null) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A correction: the old one-off is retired and the new one supersedes it, so its id changes (the Core's rule).</summary>
public sealed record CorrectIncomeOneOffCommand(ItemId ItemId, Guid Flow, LocalDate OccurredOn, Money Amount, string? Note, Money? TaxableAmount = null) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteIncomeOneOffCommand(ItemId ItemId, Guid Flow) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// What every one-off write checks first: the source, a date within it (an ended source still takes one-offs up to its
/// end, issue #22), and for an existing one-off, that it is the source's.
/// </summary>
internal static class OneOffCheck
{
    internal static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r)
        ? r : throw new InvalidOperationException();

    /// <summary>The source exists; with <paramref name="on"/>, an ended source refuses a date after its end.</summary>
    public static async Task<Result> WithinAsync(ISourceStore sources, IItemRegistry items, ItemId id, LocalDate? on, CancellationToken ct)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure(failure!);
        loaded.TryGetValue(out var source);
        return source.Item.ArchivedAt is null || on is not { } date || date <= source.Source.EndedOn
            ? Result.Success()
            : Result.Failure(new Failure(IncomeCodes.SourceEnded, "This source has ended before that date: reopen it first.",
                new Dictionary<string, object?> { ["field"] = "occurredOn" }));
    }

    /// <summary>Any current <c>income</c> flow of another category on the item is not a one-off: it answers as an unknown one.</summary>
    public static async Task<Result> ExistsAsync(IFlows flows, ItemId source, Guid id, CancellationToken ct)
    {
        var listed = await flows.ListAsync(source, Everything, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (!listed.TryGetValue(out var all))
            return Result.Failure(listed.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        return all!.Any(f => f.Id == id && f.Category == IncomeWire.OneOffCategory)
            ? Result.Success()
            : Result.Failure(new NotFoundFailure("one-off", id.ToString()));
    }

    /// <summary>
    /// Tax-per spec 3.2: the net taxable income is positive and in the amount's currency. Unlike a phase's it is not bounded by
    /// the amount: a one-off's amount is already a net, which the taxable income can exceed.
    /// </summary>
    public static Result Taxable(Money amount, Money? taxable)
    {
        if (taxable is not { } t)
            return Result.Success();
        if (t.Amount <= 0)
            return Invalid(IncomeCodes.InvalidTaxable, "A net taxable amount is more than zero.");
        return t.Currency == amount.Currency
            ? Result.Success()
            : Invalid(CoreFactCodes.CurrencyMismatch, $"This one-off is in {amount.Currency.Code}: so is its net taxable amount.");
    }

    private static Result Invalid(string code, string message)
        => Result.Failure(new Failure(code, message, new Dictionary<string, object?> { ["field"] = "taxableAmount" }));

    public static DocumentTarget Target(Guid flow) => new(IncomeDocumentTargets.OneOffKind, flow);

    public static NewFlow Flow(ItemId source, LocalDate on, Money amount, string? note)
        => new(source, on, FlowKind.Income, amount, IncomeWire.OneOffCategory, null, note);
}

[RequestHandler<RecordIncomeOneOffCommand, Guid>]
public sealed class RecordIncomeOneOffHandler(
    ISourceStore sources, IItemRegistry items, IFlows flows, IOneOffTaxableStore taxables, IUnitOfWork unitOfWork)
    : IRequestHandler<RecordIncomeOneOffCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.WithinAsync(sources, items, command.ItemId, command.OccurredOn, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        if (OneOffCheck.Taxable(command.Amount, command.TaxableAmount).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var recorded = await flows.RecordAsync(OneOffCheck.Flow(command.ItemId, command.OccurredOn, command.Amount, command.Note), ct);
        if (!recorded.TryGetValue(out var saved))
            return Result.Failure<Guid>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        await taxables.SetAsync(saved!.Id, command.ItemId, command.TaxableAmount?.Amount, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(saved.Id);
    }
}

[RequestHandler<CorrectIncomeOneOffCommand, Guid>]
public sealed class CorrectIncomeOneOffHandler(
    ISourceStore sources, IItemRegistry items, IFlows flows, IOneOffTaxableStore taxables, IDocumentLinks documents,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CorrectIncomeOneOffCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CorrectIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.WithinAsync(sources, items, command.ItemId, command.OccurredOn, ct)).TryGetFailure(out var failure)
            || (await OneOffCheck.ExistsAsync(flows, command.ItemId, command.Flow, ct)).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);
        if (OneOffCheck.Taxable(command.Amount, command.TaxableAmount).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);
        // The correction, its taxable amount and its documents' links move together, or none does.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var corrected = await flows.SupersedeAsync(command.ItemId, command.Flow,
            OneOffCheck.Flow(command.ItemId, command.OccurredOn, command.Amount, command.Note), ct);
        if (!corrected.TryGetValue(out var saved))
            return Result.Failure<Guid>(corrected.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        // The new one-off has its own id: the taxable amount moves to it, or goes when the correction sends none.
        await taxables.SetAsync(command.Flow, command.ItemId, null, ct);
        await taxables.SetAsync(saved!.Id, command.ItemId, command.TaxableAmount?.Amount, ct);
        await documents.MoveTargetAsync(OneOffCheck.Target(command.Flow), OneOffCheck.Target(saved.Id), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(saved.Id);
    }
}

[RequestHandler<DeleteIncomeOneOffCommand, bool>]
public sealed class DeleteIncomeOneOffHandler(
    ISourceStore sources, IItemRegistry items, IFlows flows, IOneOffTaxableStore taxables, IDocumentLinks documents,
    IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteIncomeOneOffCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.WithinAsync(sources, items, command.ItemId, null, ct)).TryGetFailure(out var failure)
            || (await OneOffCheck.ExistsAsync(flows, command.ItemId, command.Flow, ct)).TryGetFailure(out failure))
            return Result.Failure<bool>(failure!);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await flows.VoidAsync(command.ItemId, command.Flow, ct)).TryGetFailure(out failure))
            return Result.Failure<bool>(failure!);
        await taxables.SetAsync(command.Flow, command.ItemId, null, ct);
        await documents.UnlinkTargetAsync(OneOffCheck.Target(command.Flow), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
