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
    LocalDate From, decimal NetHourlyRate, decimal? GrossHourlyRate, decimal OvertimeRate, decimal UpkeepPer9h, decimal? LunchPrice,
    IReadOnlyList<WeekType> WeekTypes, decimal? SnackPrice = null, MealProvider LunchProvider = MealProvider.Nanny, MealProvider SnackProvider = MealProvider.Nanny,
    IReadOnlyList<AbsenceReason>? UnpaidAbsences = null)
{
    /// <summary>Week type names are stored trimmed.</summary>
    public Terms For(ItemId id)
        => new(id, From, null, NetHourlyRate, GrossHourlyRate, OvertimeRate, UpkeepPer9h, LunchPrice, [.. WeekTypes.Select(w => w with { Name = w.Name.Trim() })], SnackPrice, LunchProvider, SnackProvider, UnpaidAbsences ?? AbsenceCodes.DefaultUnpaid);

    public int ProgrammedWeeks => WeekTypes.Sum(w => w.Weeks);
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

/// <summary>#192: corrects the absence reasons deducted under the revision starting on <paramref name="From"/>, a past one included; validated months keep their figures.</summary>
public sealed record SetUnpaidAbsencesCommand(ItemId ItemId, LocalDate From, IReadOnlyList<AbsenceReason> Reasons) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The week of <paramref name="Monday"/> follows the week type of this rank; the rank the rotation gives removes the exception.</summary>
public sealed record SetWeekOverrideCommand(ItemId ItemId, LocalDate Monday, int WeekType) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ReplaceGapsCommand(ItemId ItemId, IReadOnlyList<CareGap> Gaps) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// Ends the current revision on <paramref name="On"/> and archives the item. <paramref name="End"/>: why and when it was notified, for
/// the last month's settlement; <paramref name="GrossHourlyRate"/>: the gross rate the breach indemnity needs, kept on the current terms.
/// </summary>
public sealed record CloseChildcareCommand(ItemId ItemId, LocalDate On, ContractEnd? End = null, decimal? GrossHourlyRate = null) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>What ending the contract this way would mean: seniority, notice, breach indemnity.</summary>
public sealed record ContractEndQuery(ItemId ItemId, ContractEnd End) : IQuery<ContractEndView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<ContractEndQuery, ContractEndView>]
public sealed class ContractEndHandler(IItemRegistry items, IChildcareStore store) : IRequestHandler<ContractEndQuery, ContractEndView>
{
    public async ValueTask<Result<ContractEndView>> HandleAsync(ContractEndQuery query, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ContractEndView>(failure!);
        loaded.TryGetValue(out var found);
        if (query.End.Notified < found.Terms[0].From)
            return Result.Failure<ContractEndView>(FieldErrors.Shape([new("notified", ChildcareCodes.NotifiedOutOfContract, "The contract is notified on or after it starts.")]));
        return Result.Success(ContractEndView.Of(query.End, found.Terms));
    }
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

        var terms = command.Terms with { From = revisions[^1].From, UnpaidAbsences = command.Terms.UnpaidAbsences ?? revisions[^1].UnpaidAbsences }; // a correction keeps the revision's start, and its absences when not given
        var errors = ChildcareCheck.Name(command.Name);
        errors.AddRange(ChildcareCheck.Carer(command.Carer));
        errors.AddRange(ChildcareCheck.Terms(terms));
        if (errors.Count == 0)
        {
            errors.AddRange(ChildcareCheck.Gaps(revisions[0].From, terms.ProgrammedWeeks, (await store.GapsOfAsync([command.ItemId], ct)).Select(g => g.Gap).ToList(), terms.From));
            errors.AddRange(ChildcareCheck.Overrides((await store.OverridesOfAsync([command.ItemId], ct)).Select(o => o.Override).ToList(),
                [.. revisions.SkipLast(1), terms.For(command.ItemId)]));
        }
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
        var kept = command.Terms with { UnpaidAbsences = command.Terms.UnpaidAbsences ?? revisions[^1].UnpaidAbsences };
        var errors = ChildcareCheck.Terms(command.Terms);
        if (command.Terms.From <= revisions[^1].From)
            errors.Add(new("terms.from", ChildcareCodes.RevisionNotAfter, "A revision starts after the current terms' start."));
        if (errors.Count == 0)
        {
            errors.AddRange(ChildcareCheck.Gaps(revisions[0].From, command.Terms.ProgrammedWeeks, (await store.GapsOfAsync([command.ItemId], ct)).Select(g => g.Gap).ToList(), command.Terms.From));
            errors.AddRange(ChildcareCheck.Overrides((await store.OverridesOfAsync([command.ItemId], ct)).Select(o => o.Override).ToList(),
                [.. revisions, kept.For(command.ItemId)]));
        }
        if (ChildcareCheck.Fail(errors).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.AddTermsAsync(kept.For(command.ItemId), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<SetUnpaidAbsencesCommand, ItemId>]
public sealed class SetUnpaidAbsencesHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork) : IRequestHandler<SetUnpaidAbsencesCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(SetUnpaidAbsencesCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (_, item, revisions) = found;
        if (ChildcareCheck.RequireOpen(item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        if (revisions.All(r => r.From != command.From))
            return Result.Failure<ItemId>(new NotFoundFailure("childcare.revision", $"{command.From:uuuu-MM-dd}"));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.SetUnpaidAbsencesAsync(command.ItemId, command.From, command.Reasons, ct);
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

[RequestHandler<SetWeekOverrideCommand, ItemId>]
public sealed class SetWeekOverrideHandler(IItemRegistry items, IChildcareStore store, IUnitOfWork unitOfWork) : IRequestHandler<SetWeekOverrideCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(SetWeekOverrideCommand command, CancellationToken ct = default)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var (_, item, revisions) = found;
        if (ChildcareCheck.RequireOpen(item).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        // The check names the paths of a list; a single exception's are the command's fields.
        var errors = ChildcareCheck.Overrides([new WeekOverride(command.Monday, command.WeekType)], revisions)
            .Select(e => e with { Path = e.Path.Replace("weekOverrides[0].", "") }).ToList();
        if (ChildcareCheck.Fail(errors).TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);

        // A validated month's declaration is made: none of the week's days may lie in one.
        var sunday = command.Monday.PlusDays(6);
        if ((await store.StatementsOfAsync(command.ItemId, ct)).Any(st => st.Validated && (st.Month == new YearMonth(command.Monday.Year, command.Monday.Month) || st.Month == new YearMonth(sunday.Year, sunday.Month))))
            return Result.Failure<ItemId>(FieldErrors.Shape([new("monday", ChildcareCodes.StatementValidated, "A day of this week is in a validated month: reopen it first.")]));

        // The revision in force that week: the last one starting by its Sunday, else the first (as the check reads it).
        var inForce = revisions.LastOrDefault(r => r.From <= command.Monday.PlusDays(6)) ?? revisions[0];
        var rotation = TermsPlan.Of(inForce, revisions[0].From, []).RotationOf(command.Monday);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.SetOverrideAsync(command.ItemId, command.Monday, command.WeekType == rotation ? null : command.WeekType, ct);
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
        if (command.End is { } end && (end.Notified < revisions[0].From || end.Notified > command.On))
            return Result.Failure<ItemId>(FieldErrors.Shape([new("notified", ChildcareCodes.NotifiedOutOfContract, "The contract is notified between its start and its last day.")]));
        if (command.GrossHourlyRate is <= 0)
            return Result.Failure<ItemId>(FieldErrors.Shape([new("grossHourlyRate", "request.invalid", "The gross hourly rate is above 0.")]));
        // A validated month's declaration is made: the end cannot cut into it.
        if ((await store.StatementsOfAsync(command.ItemId, ct)).Any(st => st.Validated && st.Month.OnDayOfMonth(st.Month.Calendar.GetDaysInMonth(st.Month.Year, st.Month.Month)) > command.On))
            return Result.Failure<ItemId>(FieldErrors.Shape([new("on", ChildcareCodes.EndBeforeValidated, "A validated month ends after this day: reopen it first.")]));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.SetEndAsync(command.ItemId, command.On, ct, command.End);
        if (command.GrossHourlyRate is { } gross && revisions[^1].GrossHourlyRate is null)
            await store.ReplaceCurrentTermsAsync(revisions[^1] with { GrossHourlyRate = gross }, ct);
        // An exception after the end would be refused on import: it goes with the end.
        foreach (var (_, late) in (await store.OverridesOfAsync([command.ItemId], ct)).Where(x => x.Override.Monday > command.On))
            await store.SetOverrideAsync(command.ItemId, late.Monday, null, ct);
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

/// <summary>#23: a contract created by mistake, deleted with every row of it. <paramref name="Version"/>: the one the preview read.</summary>
public sealed record DeleteChildcareCommand(ItemId ItemId, uint Version) : ICommand<ItemId>, IItemScoped, IConfirmedInBrowser
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>#23: what deleting a contract removes, and the validated months that refuse it, read before the delete.</summary>
public sealed record ChildcareDeletionQuery(ItemId ItemId) : IQuery<ItemDeletionPreview>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<DeleteChildcareCommand, ItemId>]
public sealed class DeleteChildcareHandler(IItemDeletions deletions) : IRequestHandler<DeleteChildcareCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteChildcareCommand command, CancellationToken ct = default)
        => (await deletions.DeleteAsync(command.ItemId, ChildcareModule.Key, ChildcareKinds.Childminder, command.Version, ct)).Map(() => command.ItemId);
}

[RequestHandler<ChildcareDeletionQuery, ItemDeletionPreview>]
public sealed class ChildcareDeletionHandler(IItemDeletions deletions) : IRequestHandler<ChildcareDeletionQuery, ItemDeletionPreview>
{
    public async ValueTask<Result<ItemDeletionPreview>> HandleAsync(ChildcareDeletionQuery query, CancellationToken ct = default)
        => await deletions.PreviewAsync(query.ItemId, ChildcareModule.Key, ChildcareKinds.Childminder, ct);
}
