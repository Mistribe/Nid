using Nid.Core.Contracts;
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

/// <summary>What every one-off write checks first: an open source, and for an existing one-off, that it is the source's.</summary>
internal static class OneOffCheck
{
    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r)
        ? r : throw new InvalidOperationException();

    public static async Task<Result> OpenAsync(ISourceStore sources, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure(failure!);
        loaded.TryGetValue(out var source);
        return SourceCheck.RequireOpen(source.Item);
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

    public static NewFlow Flow(ItemId source, LocalDate on, Money amount, string? note)
        => new(source, on, FlowKind.Income, amount, IncomeWire.OneOffCategory, null, note);
}

[RequestHandler<RecordIncomeOneOffCommand, Guid>]
public sealed class RecordIncomeOneOffHandler(ISourceStore sources, IItemRegistry items, IFlows flows)
    : IRequestHandler<RecordIncomeOneOffCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.OpenAsync(sources, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var recorded = await flows.RecordAsync(OneOffCheck.Flow(command.ItemId, command.OccurredOn, command.Amount, command.Note), ct);
        return recorded.TryGetValue(out var saved) ? Result.Success(saved!.Id)
            : Result.Failure<Guid>(recorded.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<CorrectIncomeOneOffCommand, Guid>]
public sealed class CorrectIncomeOneOffHandler(ISourceStore sources, IItemRegistry items, IFlows flows)
    : IRequestHandler<CorrectIncomeOneOffCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CorrectIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.OpenAsync(sources, items, command.ItemId, ct)).TryGetFailure(out var failure)
            || (await OneOffCheck.ExistsAsync(flows, command.ItemId, command.Flow, ct)).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);
        var corrected = await flows.SupersedeAsync(command.ItemId, command.Flow,
            OneOffCheck.Flow(command.ItemId, command.OccurredOn, command.Amount, command.Note), ct);
        return corrected.TryGetValue(out var saved) ? Result.Success(saved!.Id)
            : Result.Failure<Guid>(corrected.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
    }
}

[RequestHandler<DeleteIncomeOneOffCommand, bool>]
public sealed class DeleteIncomeOneOffHandler(ISourceStore sources, IItemRegistry items, IFlows flows)
    : IRequestHandler<DeleteIncomeOneOffCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteIncomeOneOffCommand command, CancellationToken ct = default)
    {
        if ((await OneOffCheck.OpenAsync(sources, items, command.ItemId, ct)).TryGetFailure(out var failure)
            || (await OneOffCheck.ExistsAsync(flows, command.ItemId, command.Flow, ct)).TryGetFailure(out failure))
            return Result.Failure<bool>(failure!);
        var voided = await flows.VoidAsync(command.ItemId, command.Flow, ct);
        return voided.TryGetFailure(out failure) ? Result.Failure<bool>(failure!) : Result.Success(true);
    }
}
