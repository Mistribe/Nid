using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Goals.Application;

/// <summary>An envelope funding a goal as a body sends it: a share in basis points (10 000 = all of it).</summary>
public sealed record FundingShare(ItemId Envelope, int ShareBp);

public sealed record CreateGoalCommand(
    string Name, string Kind, decimal? TargetAmount, int? Months, LocalDate? TargetDate, string? Note, IReadOnlyList<FundingShare> Funding,
    Visibility Visibility, IReadOnlyList<OwnerShare> Owners, ShareLevel ShareLevel = ShareLevel.View) : ICommand<ItemId>, ICreatesItem;

/// <summary>Replaces every editable field; the kind, the visibility and the owners never change (D10b). <paramref name="Version"/>: the one read with the goal.</summary>
public sealed record EditGoalCommand(
    ItemId ItemId, string Name, decimal? TargetAmount, int? Months, LocalDate? TargetDate, string? Note, IReadOnlyList<FundingShare> Funding,
    uint Version)
    : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record CloseGoalCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReopenGoalCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A free goal's amount reached on a date: one entry per date, the last one written replaces (spec §2).</summary>
public sealed record PutGoalProgressCommand(ItemId ItemId, LocalDate On, decimal Amount) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteGoalProgressCommand(ItemId ItemId, LocalDate On) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateGoalCommand, ItemId>]
public sealed class CreateGoalHandler(
    IItemRegistry items, IGoalStore goals, IEnvelopeFunding envelopes, IVisibleItems visible, IHouseholdCalendar calendar,
    IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateGoalCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateGoalCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var funding = command.Funding ?? [];
        var months = GoalCheck.Months(command.Kind, command.Months);
        if (GoalCheck.Fields(command.Name, command.Kind, command.TargetAmount, months, command.TargetDate, command.Note, today).TryGetFailure(out var failure)
            || GoalCheck.Shares(funding).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        var person = actor.Current!.PersonId!.Value;
        var read = await GoalCheck.SeenAsync(envelopes, person, funding.Select(f => f.Envelope).ToList(), today, ct);
        if (!read.TryGetValue(out var seen))
            return Result.Failure<ItemId>(read.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        if (GoalCheck.Linkable(funding, seen!, []).TryGetFailure(out failure)
            || (await GoalCheck.RoomAsync(goals, visible, person, null, funding, seen!, located: true, [], ct)).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("goals", command.Kind, command.Name, command.Visibility, null, command.Owners, command.ShareLevel), ct);
        if (!created.TryGetValue(out var id))
            return created; // disposed without commit: nothing is left
        await goals.CreateAsync(new Goal(id, command.Kind, command.TargetAmount, months, command.TargetDate, command.Note, 0), ct);
        await goals.ReplaceFundingAsync(id, funding.Select(f => new Funding(id, f.Envelope, f.ShareBp)).ToList(), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

/// <summary>D10c: the links to envelopes the editor sees are replaced; the others are kept as they are, never shown.</summary>
[RequestHandler<EditGoalCommand, ItemId>]
public sealed class EditGoalHandler(
    IItemRegistry items, IGoalStore goals, IEnvelopeFunding envelopes, IVisibleItems visible, IHouseholdCalendar calendar,
    IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<EditGoalCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditGoalCommand command, CancellationToken ct = default)
    {
        var loaded = await GoalCheck.LoadAsync(goals, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (goal, item) = found;
        if (GoalCheck.RequireOpen(item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        // Checked here too: an edit that leaves the goal row as it is writes nothing, so the store's token would not see it.
        if (goal.Version != command.Version)
            return Result.Failure<ItemId>(new Failure(GoalsCodes.Conflict, "The goal changed since it was read.", null));

        var today = await calendar.TodayAsync(ct);
        var funding = command.Funding ?? [];
        var months = GoalCheck.Months(goal.Kind, command.Months);
        if (GoalCheck.Fields(command.Name, goal.Kind, command.TargetAmount, months, command.TargetDate, command.Note, today, goal.TargetDate).TryGetFailure(out failure)
            || GoalCheck.Shares(funding).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        var person = actor.Current!.PersonId!.Value;
        var stored = await goals.FundingOfAsync([command.ItemId], ct);
        var read = await GoalCheck.SeenAsync(envelopes, person, funding.Select(f => f.Envelope).Concat(stored.Select(f => f.Envelope)).ToList(), today, ct);
        if (!read.TryGetValue(out var seen))
            return Result.Failure<ItemId>(read.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        if (GoalCheck.Linkable(funding, seen!, stored.Select(f => f.Envelope).ToList()).TryGetFailure(out failure)
            || (await GoalCheck.RoomAsync(goals, visible, person, command.ItemId, funding, seen!, located: true, stored, ct)).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        var hidden = stored.Where(f => !seen!.ContainsKey(f.Envelope));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        if (command.Name != item.DisplayName)
        {
            var renamed = await items.ReplaceAsync(command.ItemId, command.Name, item.Visibility,
                item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (renamed.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }
        var updated = await goals.UpdateAsync(goal with
        {
            Version = command.Version, TargetAmount = command.TargetAmount, Months = months, TargetDate = command.TargetDate, Note = command.Note,
        }, ct);
        if (updated.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await goals.ReplaceFundingAsync(command.ItemId,
            funding.Select(f => new Funding(command.ItemId, f.Envelope, f.ShareBp)).Concat(hidden).ToList(), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

/// <summary>D12: closing archives the item; the goal no longer counts in the shares of its envelopes.</summary>
[RequestHandler<CloseGoalCommand, ItemId>]
public sealed class CloseGoalHandler(IItemRegistry items, IGoalStore goals, IUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CloseGoalCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CloseGoalCommand command, CancellationToken ct = default)
    {
        var loaded = await GoalCheck.LoadAsync(goals, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (GoalCheck.RequireOpen(found.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var archived = await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct);
        if (archived.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

/// <summary>D10, D12: a goal reopens only if its shares still fit on the envelopes the actor sees.</summary>
[RequestHandler<ReopenGoalCommand, ItemId>]
public sealed class ReopenGoalHandler(
    IItemRegistry items, IGoalStore goals, IEnvelopeFunding envelopes, IVisibleItems visible, IHouseholdCalendar calendar,
    IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<ReopenGoalCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReopenGoalCommand command, CancellationToken ct = default)
    {
        var loaded = await GoalCheck.LoadAsync(goals, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Item.ArchivedAt is null)
            return Result.Failure<ItemId>(new Failure(GoalsCodes.GoalOpen, "This goal is not closed.", null));

        var person = actor.Current!.PersonId!.Value;
        var stored = await goals.FundingOfAsync([command.ItemId], ct);
        var read = await GoalCheck.SeenAsync(envelopes, person, stored.Select(f => f.Envelope).ToList(), await calendar.TodayAsync(ct), ct);
        if (!read.TryGetValue(out var seen))
            return Result.Failure<ItemId>(read.TryGetFailure(out failure) ? failure! : throw new InvalidOperationException());
        var links = stored.Where(f => seen!.ContainsKey(f.Envelope)).Select(f => new FundingShare(f.Envelope, f.ShareBp)).ToList();
        if ((await GoalCheck.RoomAsync(goals, visible, person, command.ItemId, links, seen!, located: false, [], ct)).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var reopened = await items.UnarchiveAsync(command.ItemId, ct);
        if (reopened.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<PutGoalProgressCommand, ItemId>]
public sealed class PutGoalProgressHandler(IItemRegistry items, IGoalStore goals, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<PutGoalProgressCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(PutGoalProgressCommand command, CancellationToken ct = default)
    {
        var loaded = await GoalCheck.LoadAsync(goals, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (GoalCheck.RequireOpen(found.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        if (command.On > await calendar.TodayAsync(ct))
            return Fail(GoalsCodes.ProgressInFuture, "A progress entry is not dated in the future.", "on");
        if (command.Amount < 0)
            return Fail(GoalsCodes.InvalidTarget, "An amount reached is zero or more.", "amount");
        if (GoalCheck.Amount(command.Amount, "amount").TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        // Spec §2: a goal funded by envelopes reads them, not entries; any link counts, seen by the actor or not.
        if ((await goals.FundingOfAsync([command.ItemId], ct)).Count > 0)
            return Fail(GoalsCodes.MixedFunding, "This goal is funded by envelopes: remove them to record its progress by hand.", null);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await goals.PutEntryAsync(new GoalEntry(command.ItemId, command.On, command.Amount), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }

    private static Result<ItemId> Fail(string code, string message, string? field)
        => Result.Failure<ItemId>(new Failure(code, message, field is null ? null : new Dictionary<string, object?> { ["field"] = field }));
}

[RequestHandler<DeleteGoalProgressCommand, ItemId>]
public sealed class DeleteGoalProgressHandler(IItemRegistry items, IGoalStore goals, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteGoalProgressCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteGoalProgressCommand command, CancellationToken ct = default)
    {
        var loaded = await GoalCheck.LoadAsync(goals, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (GoalCheck.RequireOpen(found.Item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        if (!await goals.DeleteEntryAsync(command.ItemId, command.On, ct))
            return Result.Failure<ItemId>(new NotFoundFailure("progress entry", command.On.ToString("uuuu-MM-dd", null)));
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}
