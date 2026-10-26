using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Income.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

public sealed record RecordIncomeOneOffCommand(ItemId ItemId, LocalDate OccurredOn, Money Amount, string? Note) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A correction: the old one-off is retired and the new one supersedes it, so its id changes (the Core's rule).</summary>
public sealed record CorrectIncomeOneOffCommand(ItemId ItemId, Guid Flow, LocalDate OccurredOn, Money Amount, string? Note) : ICommand<Guid>, IItemScoped
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

    public static DocumentTarget Target(Guid flow) => new(IncomeDocumentTargets.OneOffKind, flow);

    public static NewFlow Flow(ItemId source, LocalDate on, Money amount, string? note)
        => new(source, on, FlowKind.Income, amount, IncomeWire.OneOffCategory, null, note);
}

[RequestHandler<RecordIncomeOneOffCommand, Guid>]
public sealed class RecordIncomeOneOffHandler(ISourceStore sources, IItemRegistry items, IFlows flows)
    : IRequestHandler<RecordIncomeOneOffCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.WithinAsync(sources, items, command.ItemId, command.OccurredOn, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var recorded = await flows.RecordAsync(OneOffCheck.Flow(command.ItemId, command.OccurredOn, command.Amount, command.Note), ct);
        return recorded.TryGetValue(out var saved) ? Result.Success(saved!.Id)
            : Result.Failure<Guid>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<CorrectIncomeOneOffCommand, Guid>]
public sealed class CorrectIncomeOneOffHandler(ISourceStore sources, IItemRegistry items, IFlows flows, IDocumentLinks documents, IUnitOfWork unitOfWork)
    : IRequestHandler<CorrectIncomeOneOffCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CorrectIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.WithinAsync(sources, items, command.ItemId, command.OccurredOn, ct)).TryGetFailure(out var failure)
            || (await OneOffCheck.ExistsAsync(flows, command.ItemId, command.Flow, ct)).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);
        // The correction and its documents' links move together, or neither does.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var corrected = await flows.SupersedeAsync(command.ItemId, command.Flow,
            OneOffCheck.Flow(command.ItemId, command.OccurredOn, command.Amount, command.Note), ct);
        if (!corrected.TryGetValue(out var saved))
            return Result.Failure<Guid>(corrected.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        await documents.MoveTargetAsync(OneOffCheck.Target(command.Flow), OneOffCheck.Target(saved!.Id), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(saved.Id);
    }
}

[RequestHandler<DeleteIncomeOneOffCommand, bool>]
public sealed class DeleteIncomeOneOffHandler(ISourceStore sources, IItemRegistry items, IFlows flows, IDocumentLinks documents, IUnitOfWork unitOfWork)
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
        await documents.UnlinkTargetAsync(OneOffCheck.Target(command.Flow), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
