using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Childcare.Application;

public sealed record CarerInput(string Name, string? Pajemploi, string? Approval);

public sealed record TermsInput(
    LocalDate From, decimal NetHourlyRate, decimal? GrossHourlyRate, decimal OvertimeRate, decimal UpkeepPer9h, decimal? MealPrice, int ProgrammedWeeks,
    IReadOnlyList<WeekdaySlot> Schedule)
{
    public Terms For(ItemId id) => new(id, From, null, NetHourlyRate, GrossHourlyRate, OvertimeRate, UpkeepPer9h, MealPrice, ProgrammedWeeks, Schedule);
}

public sealed record CreateChildcareCommand(
    string Name, PersonId Child, CarerInput Carer, TermsInput Terms, IReadOnlyList<CareGap> Gaps, Visibility Visibility, IReadOnlyList<OwnerShare> Owners,
    ShareLevel ShareLevel = ShareLevel.View) : ICommand<ItemId>, ICreatesItem;

/// <summary>Corrects the arrangement and its current revision in place; the revision keeps its start. <paramref name="Version"/>: the one read.</summary>
public sealed record EditChildcareCommand(ItemId ItemId, string Name, CarerInput Carer, TermsInput Terms, uint Version) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec §1.8: a new revision from <c>Terms.From</c>, after the current one's start; the current one ends the day before.</summary>
public sealed record ReviseChildcareCommand(ItemId ItemId, TermsInput Terms) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReplaceGapsCommand(ItemId ItemId, IReadOnlyList<CareGap> Gaps) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Ends the current revision on <paramref name="On"/> and archives the item.</summary>
public sealed record CloseChildcareCommand(ItemId ItemId, LocalDate On) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReopenChildcareCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateChildcareCommand, ItemId>]
public sealed class CreateChildcareHandler(IItemRegistry items, IChildcareStore store, IPersonRegistry persons, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateChildcareCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateChildcareCommand command, CancellationToken ct = default)
    {
        var errors = ChildcareCheck.Name(command.Name);
        errors.AddRange(await ChildcareCheck.ChildAsync(persons, command.Child, ct));
        errors.AddRange(ChildcareCheck.Carer(command.Carer));
        errors.AddRange(ChildcareCheck.Terms(command.Terms));
        if (errors.Count == 0) errors.AddRange(ChildcareCheck.Gaps(command.Terms.From, command.Terms.ProgrammedWeeks, command.Gaps));
        if (ChildcareCheck.Fail(errors).TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem(ChildcareModule.Key, ChildcareKinds.Childminder, command.Name.Trim(), command.Visibility, null, command.Owners, command.ShareLevel), ct);
        if (!created.TryGetValue(out var id))
            return created; // disposed without commit: nothing is left
        await store.CreateAsync(new Arrangement(id, command.Child, command.Carer.Name.Trim(), command.Carer.Pajemploi, command.Carer.Approval, 0),
            command.Terms.For(id), ct);
        await store.ReplaceGapsAsync(id, command.Gaps, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditChildcareCommand, ItemId>]
public sealed class EditChildcareHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork) : IRequestHandler<EditChildcareCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditChildcareCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (arrangement, item, revisions) = found;
        if (ChildcareCheck.RequireOpen(item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        // Checked here too: the store's token only sees a write that reaches the row.
        if (arrangement.Version != command.Version)
            return Result.Failure<ItemId>(new Failure(ChildcareCodes.Conflict, "The arrangement changed since it was read.", null));

        var terms = command.Terms with { From = revisions[^1].From }; // a correction keeps the revision's start
        var errors = ChildcareCheck.Name(command.Name);
        errors.AddRange(ChildcareCheck.Carer(command.Carer));
        errors.AddRange(ChildcareCheck.Terms(terms));
        if (errors.Count == 0)
            errors.AddRange(ChildcareCheck.Gaps(revisions[0].From, terms.ProgrammedWeeks, (await store.GapsOfAsync([command.ItemId], ct)).Select(g => g.Gap).ToList(), terms.From));
        if (ChildcareCheck.Fail(errors).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        if (command.Name.Trim() != item.DisplayName)
        {
            var renamed = await items.ReplaceAsync(command.ItemId, command.Name.Trim(), item.Visibility,
                item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (renamed.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }
        var updated = await store.UpdateAsync(arrangement with
        {
            CarerName = command.Carer.Name.Trim(), CarerPajemploi = command.Carer.Pajemploi, CarerApproval = command.Carer.Approval, Version = command.Version,
        }, ct);
        if (updated.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await store.ReplaceCurrentTermsAsync(terms.For(command.ItemId), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<ReviseChildcareCommand, ItemId>]
public sealed class ReviseChildcareHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork) : IRequestHandler<ReviseChildcareCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReviseChildcareCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (_, item, revisions) = found;
        if (ChildcareCheck.RequireOpen(item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        var errors = ChildcareCheck.Terms(command.Terms);
        if (command.Terms.From <= revisions[^1].From)
            errors.Add(new("terms.from", ChildcareCodes.RevisionNotAfter, "A revision starts after the current terms' start."));
        if (errors.Count == 0)
            errors.AddRange(ChildcareCheck.Gaps(revisions[0].From, command.Terms.ProgrammedWeeks, (await store.GapsOfAsync([command.ItemId], ct)).Select(g => g.Gap).ToList(), command.Terms.From));
        if (ChildcareCheck.Fail(errors).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.AddTermsAsync(command.Terms.For(command.ItemId), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<ReplaceGapsCommand, ItemId>]
public sealed class ReplaceGapsHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork) : IRequestHandler<ReplaceGapsCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReplaceGapsCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (_, item, revisions) = found;
        if (ChildcareCheck.RequireOpen(item).TryGetFailure(out failure)
            || ChildcareCheck.Fail(ChildcareCheck.Gaps(revisions[0].From, revisions[^1].ProgrammedWeeks, command.Gaps, revisions[^1].From)).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.ReplaceGapsAsync(command.ItemId, command.Gaps, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<CloseChildcareCommand, ItemId>]
public sealed class CloseChildcareHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<CloseChildcareCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CloseChildcareCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (_, item, revisions) = found;
        if (ChildcareCheck.RequireOpen(item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        if (command.On < revisions[^1].From)
            return Result.Failure<ItemId>(FieldErrors.Shape([new("on", ChildcareCodes.EndBeforeTerms, "The end comes on or after the current terms' start.")]));
        // A validated month's declaration is made: the end cannot cut into it.
        if ((await store.StatementsOfAsync(command.ItemId, ct)).Any(st => st.Validated && st.Month.OnDayOfMonth(st.Month.Calendar.GetDaysInMonth(st.Month.Year, st.Month.Month)) > command.On))
            return Result.Failure<ItemId>(FieldErrors.Shape([new("on", ChildcareCodes.EndBeforeValidated, "A validated month ends after this day: reopen it first.")]));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.SetEndAsync(command.ItemId, command.On, ct);
        var archived = await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct);
        if (archived.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<ReopenChildcareCommand, ItemId>]
public sealed class ReopenChildcareHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork) : IRequestHandler<ReopenChildcareCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReopenChildcareCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Item.ArchivedAt is null)
            return Result.Failure<ItemId>(new Failure(ChildcareCodes.Open, "This arrangement is not closed.", null));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.SetEndAsync(command.ItemId, null, ct);
        var reopened = await items.UnarchiveAsync(command.ItemId, ct);
        if (reopened.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}
