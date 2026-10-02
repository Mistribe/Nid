using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

public sealed record CloseIncomeSourceCommand(ItemId ItemId, LocalDate EndedOn) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReopenIncomeSourceCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// Ending a source (spec §4): the phases still running on <c>EndedOn</c> are cut there, the source is marked ended and
/// archived, all in one transaction. A phase that starts on or after <c>EndedOn</c> would never run: ending is refused.
/// </summary>
[RequestHandler<CloseIncomeSourceCommand, ItemId>]
public sealed class CloseIncomeSourceHandler(
    ISourceStore sources, IItemRegistry items, IPhaseStore phases, IHouseholdCalendar calendar, IUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CloseIncomeSourceCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CloseIncomeSourceCommand command, CancellationToken ct = default)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var source);
        if (SourceCheck.RequireOpen(source.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        if (command.EndedOn > await calendar.TodayAsync(ct))
            return Fail(CoreFactCodes.DateInFuture, "A source cannot end in the future.");
        var all = await phases.ForSourceAsync(command.ItemId, ct);
        if (all.Any(p => p.StartsOn >= command.EndedOn))
            return Fail(IncomeCodes.InvalidSource, "A phase starts on or after that date: delete or correct it before ending the source.");

        await using var transaction = await unitOfWork.BeginAsync(ct);
        try
        {
            // A phase already over by then (its end, or its last occurrence's, on or before the date) needs no cut.
            foreach (var phase in all.Where(p => IncomeMath.EffectiveEnd(p) is not { } end || end > command.EndedOn))
                await phases.UpdateAsync(phase with { EndsOn = command.EndedOn }, ct);
        }
        catch (PhaseConflictException)
        {
            return Result.Failure<ItemId>(OpenSource.Conflict()); // disposed without commit
        }
        var archived = await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct);
        if (archived.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await sources.SetEndedOnAsync(command.ItemId, command.EndedOn, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }

    private static Result<ItemId> Fail(string code, string message)
        => Result.Failure<ItemId>(new Failure(code, message, new Dictionary<string, object?> { ["field"] = "endedOn" }));
}

/// <summary>The phases stay as the ending left them (spec §4); adding a phase afterwards is the person's choice.</summary>
[RequestHandler<ReopenIncomeSourceCommand, ItemId>]
public sealed class ReopenIncomeSourceHandler(ISourceStore sources, IItemRegistry items, IUnitOfWork unitOfWork)
    : IRequestHandler<ReopenIncomeSourceCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReopenIncomeSourceCommand command, CancellationToken ct = default)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var source);
        if (source.Item.ArchivedAt is null)
            return Result.Failure<ItemId>(new Failure(IncomeCodes.SourceOpen, "This source has not ended.", null));
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var reopened = await items.UnarchiveAsync(command.ItemId, ct);
        if (reopened.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await sources.SetEndedOnAsync(command.ItemId, null, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}
