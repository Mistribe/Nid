using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Childcare.Application;

/// <summary>A day as entered: times and meal count only on a present day, a reason only on an absent planned one.</summary>
public sealed record DayInput(LocalDate Date, bool Present, LocalTime? Arrival, LocalTime? Departure, bool Meal, AbsenceReason? Absence);

/// <summary>Decision 29: a month entered as its care days and total hours; <c>Absence</c>: the reason of the planned days left over.</summary>
public sealed record MonthSummaryInput(int CareDays, decimal Hours, AbsenceReason? Absence);

/// <summary>
/// Decision 13: saves the month as a draft (created on its first save). <paramref name="Version"/>: the one read, 0 when nothing was
/// stored. <paramref name="Summary"/>: decision 29, replaces <paramref name="Days"/>. <paramref name="Declared"/>: decision 30.
/// </summary>
public sealed record SaveStatementCommand(
    ItemId ItemId, YearMonth Month, IReadOnlyList<DayInput> Days, decimal Mileage, decimal? Cmg, uint Version, DeclaredFigures? Declared = null, MonthSummaryInput? Summary = null)
    : ICommand<ChildcareStatementView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec §1.4, decisions 14 and 17: freezes the month's figures.</summary>
public sealed record ValidateStatementCommand(ItemId ItemId, YearMonth Month, uint Version) : ICommand<ChildcareStatementView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec §5: back to draft while the next month is not validated.</summary>
public sealed record ReopenStatementCommand(ItemId ItemId, YearMonth Month) : ICommand<ChildcareStatementView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Decision 13 « Repartir du planning »: deletes the draft.</summary>
public sealed record DiscardStatementCommand(ItemId ItemId, YearMonth Month) : ICommand<ChildcareStatementView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<SaveStatementCommand, ChildcareStatementView>]
public sealed class SaveStatementHandler(StatementAssembly assembly, IChildcareStore store, IUnitOfWork unitOfWork, IActorAccessor actor)
    : IRequestHandler<SaveStatementCommand, ChildcareStatementView>
{
    public async ValueTask<Result<ChildcareStatementView>> HandleAsync(SaveStatementCommand command, CancellationToken ct = default)
    {
        var loaded = await assembly.ContextAsync(command.ItemId, command.Month, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        loaded.TryGetValue(out var context);

        var entered = command.Days
            .Select(d => new StatementDay(d.Date, false, d.Present, d.Present ? d.Arrival : null, d.Present ? d.Departure : null, d.Present && d.Meal, d.Absence))
            .ToList();
        if (command.Summary is { } summary)
        {
            var (spread, problems) = Domain.StatementSummary.Spread(assembly.Prepared(context!), context!.Contract, summary.CareDays, summary.Hours, summary.Absence);
            if (ChildcareCheck.Fail(problems.Select(p => new FieldError(p.Field, p.Code, StatementAssembly.Message(p.Code))).ToList()).TryGetFailure(out failure))
                return Result.Failure<ChildcareStatementView>(failure!);
            entered = [.. spread];
        }
        var days = assembly.Replanned(context!, context!.Chosen, entered);
        // Decision 14: a correction the plan changed under, sent back untouched, gives way to the template's day.
        var prepared = assembly.Prepared(context).ToDictionary(d => d.Date);
        var saved = context.StoredMonth is { Validated: false } && await store.FindStatementAsync(command.ItemId, command.Month, ct) is { } draft
            ? draft.Days.ToDictionary(d => d.Date)
            : [];
        days = days.Select(d => saved.TryGetValue(d.Date, out var s) && s.Planned != d.Planned && Same(s, d) && prepared.TryGetValue(d.Date, out var p) ? p : d).ToList();
        var errors = Domain.StatementCheck.Days(command.Month, days).Select(p => new FieldError(p.Field, p.Code, StatementAssembly.Message(p.Code))).ToList();
        if (command.Mileage < 0) errors.Add(new("mileage", FieldErrors.ShapeCode, "Zero or more."));
        if (command.Cmg < 0) errors.Add(new("cmg", FieldErrors.ShapeCode, "Zero or more."));
        if (command.Declared is { AnyNegative: true }) errors.Add(new("declared", FieldErrors.ShapeCode, "Zero or more."));
        if (ChildcareCheck.Fail(errors).TryGetFailure(out failure))
            return Result.Failure<ChildcareStatementView>(failure!);

        var written = await StatementResult.InTransactionAsync(unitOfWork,
            () => store.SaveDraftAsync(new Statement(command.ItemId, command.Month, false, command.Mileage, command.Cmg,
                days.Where(d => !prepared.TryGetValue(d.Date, out var p) || p != d).ToList(), null, command.Version, null, command.Declared), ct), ct);
        if (written.TryGetFailure(out failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        return await StatementResult.ViewAsync(assembly, actor, command.ItemId, command.Month, ct);
    }

    private static bool Same(StatementDay a, StatementDay b)
        => a.Present == b.Present && a.Arrival == b.Arrival && a.Departure == b.Departure && a.Meal == b.Meal && a.Absence == b.Absence;
}

[RequestHandler<ValidateStatementCommand, ChildcareStatementView>]
public sealed class ValidateStatementHandler(StatementAssembly assembly, IChildcareStore store, IUnitOfWork unitOfWork, IActorAccessor actor)
    : IRequestHandler<ValidateStatementCommand, ChildcareStatementView>
{
    public async ValueTask<Result<ChildcareStatementView>> HandleAsync(ValidateStatementCommand command, CancellationToken ct = default)
    {
        var loaded = await assembly.ContextAsync(command.ItemId, command.Month, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        loaded.TryGetValue(out var context);
        var stored = context!.StoredMonth;
        if (stored is { Validated: true })
            return Result.Failure<ChildcareStatementView>(new Failure(ChildcareCodes.StatementValidated, "The statement is already validated.", null));
        var previous = command.Month.PlusMonths(-1);
        if (Domain.StatementContext.Months(context.Contract.Start, context.Contract.End, context.Today).Contains(previous)
            && !context.Stored.Any(s => s.Month == previous && s.Validated))
            return Result.Failure<ChildcareStatementView>(new Failure(ChildcareCodes.PreviousNotValidated,
                "Validate the previous month first: this one's sick-child quota and first week depend on it.", null));
        if (stored?.Cmg is null)
            return Result.Failure<ChildcareStatementView>(FieldErrors.Shape([new("cmg", ChildcareCodes.CmgRequired, "Enter the CMG of the Pajemploi summary, 0 if none.")]));

        var (days, stale) = await assembly.DaysAsync(context, ct);
        var evaluation = await assembly.EvaluateAsync(context, days, stale, stored.Mileage, stored.Cmg, stored.Declared, ct);
        if (evaluation.Figures is not { } figures)
            return Result.Failure<ChildcareStatementView>(new Failure(ChildcareCodes.StatementIncomplete,
                "A day is inconsistent or a month of the reference period is not validated.", null));

        var validated = await StatementResult.InTransactionAsync(unitOfWork, () => store.ValidateAsync(command.ItemId, command.Month, figures, days, command.Version, ct), ct);
        if (validated.TryGetFailure(out failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        return await StatementResult.ViewAsync(assembly, actor, command.ItemId, command.Month, ct);
    }
}

[RequestHandler<ReopenStatementCommand, ChildcareStatementView>]
public sealed class ReopenStatementHandler(StatementAssembly assembly, IChildcareStore store, IUnitOfWork unitOfWork, IActorAccessor actor)
    : IRequestHandler<ReopenStatementCommand, ChildcareStatementView>
{
    public async ValueTask<Result<ChildcareStatementView>> HandleAsync(ReopenStatementCommand command, CancellationToken ct = default)
    {
        var loaded = await assembly.ContextAsync(command.ItemId, command.Month, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        loaded.TryGetValue(out var context);
        if (context!.Stored.Any(s => s.Month == command.Month.PlusMonths(1) && s.Validated))
            return Result.Failure<ChildcareStatementView>(new Failure(ChildcareCodes.NextValidated, "The next month is validated: reopen it first.", null));

        if (context.StoredMonth is { Validated: true })
            await StatementResult.InTransactionAsync(unitOfWork, async () => { await store.ReopenAsync(command.ItemId, command.Month, ct); return Result.Success(); }, ct);
        return await StatementResult.ViewAsync(assembly, actor, command.ItemId, command.Month, ct);
    }
}

[RequestHandler<DiscardStatementCommand, ChildcareStatementView>]
public sealed class DiscardStatementHandler(StatementAssembly assembly, IChildcareStore store, IUnitOfWork unitOfWork, IActorAccessor actor)
    : IRequestHandler<DiscardStatementCommand, ChildcareStatementView>
{
    public async ValueTask<Result<ChildcareStatementView>> HandleAsync(DiscardStatementCommand command, CancellationToken ct = default)
    {
        var loaded = await assembly.ContextAsync(command.ItemId, command.Month, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        loaded.TryGetValue(out var context);
        if (context!.StoredMonth is { Validated: true })
            return Result.Failure<ChildcareStatementView>(new Failure(ChildcareCodes.StatementValidated, "Reopen the statement first.", null));

        await StatementResult.InTransactionAsync(unitOfWork, async () => { await store.DiscardAsync(command.ItemId, command.Month, ct); return Result.Success(); }, ct);
        return await StatementResult.ViewAsync(assembly, actor, command.ItemId, command.Month, ct);
    }
}

/// <summary>Every statement write answers with the month as it now reads.</summary>
internal static class StatementResult
{
    /// <summary>The write in its own transaction, closed before the month is read back.</summary>
    public static async Task<Result> InTransactionAsync(IUnitOfWork unitOfWork, Func<Task<Result>> write, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var result = await write();
        if (result.IsSuccess)
            await transaction.CommitAsync(ct);
        return result;
    }

    public static async Task<Result<ChildcareStatementView>> ViewAsync(StatementAssembly assembly, IActorAccessor actor, ItemId id, YearMonth month, CancellationToken ct)
    {
        var context = await assembly.ContextAsync(id, month, ct);
        if (context.TryGetFailure(out var failure))
            return Result.Failure<ChildcareStatementView>(failure!);
        context.TryGetValue(out var found);
        return Result.Success(await assembly.ViewAsync(actor.Current!.PersonId!.Value, found!, ct));
    }
}
