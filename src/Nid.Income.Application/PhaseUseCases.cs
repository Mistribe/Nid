using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Application;

public sealed record AddIncomePhaseCommand(ItemId ItemId, PhaseDraft Phase) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A correction of a phase in place; <paramref name="Version"/> is the one the client read (spec §4).</summary>
public sealed record EditIncomePhaseCommand(ItemId ItemId, Guid Phase, PhaseDraft Values, uint Version) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteIncomePhaseCommand(ItemId ItemId, Guid Phase, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A change of phase (a raise): the phase of that label running on <paramref name="EffectiveOn"/> ends there, the next starts there.</summary>
public sealed record ChangeIncomePhaseCommand(
    ItemId ItemId, string Label, LocalDate EffectiveOn, Money NetAmount, Duration? Every, Money? GrossAmount, string? Note)
    : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record GetIncomeExpectedQuery(ItemId ItemId, LocalDate From, LocalDate To) : IQuery<IncomeExpected>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>What every phase write checks first: the source exists and is open; its currency and its phases, read once.</summary>
internal static class OpenSource
{
    public static async Task<Result<(Currency Currency, IReadOnlyList<IncomePhase> Phases)>> LoadAsync(
        ISourceStore sources, IItemRegistry items, IPhaseStore phases, ItemId id, CancellationToken ct)
    {
        var loaded = await SourceCheck.LoadAsync(sources, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<(Currency, IReadOnlyList<IncomePhase>)>(failure!);
        loaded.TryGetValue(out var source);
        if (SourceCheck.RequireOpen(source.Item).TryGetFailure(out failure))
            return Result.Failure<(Currency, IReadOnlyList<IncomePhase>)>(failure!);
        return Result.Success((SourceCheck.CurrencyOf(source.Item), await phases.ForSourceAsync(id, ct)));
    }

    /// <summary>A phase alone, then against the source's other phases of its label.</summary>
    public static Result Check(PhaseDraft d, Currency currency, IEnumerable<IncomePhase> others)
        => PhaseRules.Check(d.Label, d.StartsOn, d.EndsOn, d.NetAmount, d.Every, d.GrossAmount, d.MaxCycles, d.Note, currency)
            .TryGetFailure(out var failure)
            ? Result.Failure(failure!)
            : PhaseRules.NoOverlap(d.Label.Trim(), d.StartsOn, d.EndsOn, d.Every, d.MaxCycles, others);

    public static Failure Conflict()
        => new(IncomeCodes.PhaseConflict, "Someone else changed this phase since you read it; read it again.", null);

    public static Failure Missing(Guid phase) => new NotFoundFailure("income phase", phase.ToString());
}

[RequestHandler<AddIncomePhaseCommand, Guid>]
public sealed class AddIncomePhaseHandler(IItemRegistry items, ISourceStore sources, IPhaseStore phases, IUnitOfWork unitOfWork)
    : IRequestHandler<AddIncomePhaseCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AddIncomePhaseCommand command, CancellationToken ct = default)
    {
        var loaded = await OpenSource.LoadAsync(sources, items, phases, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var source);
        var d = command.Phase;
        if (OpenSource.Check(d, source.Currency, source.Phases).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var added = await phases.AddAsync(new IncomePhase(Guid.CreateVersion7(), command.ItemId, d.Label.Trim(), d.StartsOn, d.EndsOn,
            d.NetAmount, d.Every, d.GrossAmount, d.MaxCycles, d.Note), null, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(added.Id);
    }
}

[RequestHandler<EditIncomePhaseCommand, Guid>]
public sealed class EditIncomePhaseHandler(IItemRegistry items, ISourceStore sources, IPhaseStore phases, IUnitOfWork unitOfWork)
    : IRequestHandler<EditIncomePhaseCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditIncomePhaseCommand command, CancellationToken ct = default)
    {
        var loaded = await OpenSource.LoadAsync(sources, items, phases, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var source);
        if (!source.Phases.Any(p => p.Id == command.Phase))
            return Result.Failure<Guid>(OpenSource.Missing(command.Phase));
        var d = command.Values;
        if (OpenSource.Check(d, source.Currency, source.Phases.Where(p => p.Id != command.Phase)).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        try
        {
            // The client's version, not the one just read: a change made since the client read it is a conflict.
            await phases.UpdateAsync(new IncomePhase(command.Phase, command.ItemId, d.Label.Trim(), d.StartsOn, d.EndsOn,
                d.NetAmount, d.Every, d.GrossAmount, d.MaxCycles, d.Note, command.Version), ct);
        }
        catch (PhaseConflictException)
        {
            return Result.Failure<Guid>(OpenSource.Conflict()); // disposed without commit
        }
        await transaction.CommitAsync(ct);
        return Result.Success(command.Phase);
    }
}

[RequestHandler<DeleteIncomePhaseCommand, bool>]
public sealed class DeleteIncomePhaseHandler(IItemRegistry items, ISourceStore sources, IPhaseStore phases, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteIncomePhaseCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteIncomePhaseCommand command, CancellationToken ct = default)
    {
        var loaded = await OpenSource.LoadAsync(sources, items, phases, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var source);
        if (!source.Phases.Any(p => p.Id == command.Phase))
            return Result.Failure<bool>(OpenSource.Missing(command.Phase));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        try
        {
            await phases.DeleteAsync(command.ItemId, command.Phase, command.Version, ct);
        }
        catch (PhaseConflictException)
        {
            return Result.Failure<bool>(OpenSource.Conflict());
        }
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}

[RequestHandler<ChangeIncomePhaseCommand, Guid>]
public sealed class ChangeIncomePhaseHandler(IItemRegistry items, ISourceStore sources, IPhaseStore phases, IUnitOfWork unitOfWork)
    : IRequestHandler<ChangeIncomePhaseCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(ChangeIncomePhaseCommand command, CancellationToken ct = default)
    {
        var loaded = await OpenSource.LoadAsync(sources, items, phases, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var source);

        var label = command.Label?.Trim();
        var running = source.Phases.Where(p => p.Label == label && IncomeMath.IsActive(p, command.EffectiveOn)).ToList();
        if (running.Count == 0)
            return Fail(IncomeCodes.NoRunningPhase, "label", $"No '{label}' phase runs on that date.");
        var current = running.Single(); // NoOverlap guarantees at most one
        if (command.EffectiveOn <= current.StartsOn)
            return Fail(IncomeCodes.InvalidPhase, "effectiveOn", "A change takes effect after the phase it replaces starts.");

        // The next phase keeps the old one's effective end (a fixed-term contract keeps its end; a capped phase ends where
        // its last occurrence did), never its max_cycles: it is never open-ended when the old one was bounded.
        var next = new IncomePhase(Guid.CreateVersion7(), command.ItemId, current.Label, command.EffectiveOn, IncomeMath.EffectiveEnd(current),
            command.NetAmount, command.Every ?? current.Every, command.GrossAmount, null, command.Note);
        if (PhaseRules.Check(next.Label, next.StartsOn, next.EndsOn, next.NetAmount, next.Every, next.GrossAmount, next.MaxCycles,
                next.Note, source.Currency).TryGetFailure(out failure)
            || PhaseRules.NoOverlap(next.Label, next.StartsOn, next.EndsOn, next.Every, next.MaxCycles, source.Phases.Where(p => p.Id != current.Id)).TryGetFailure(out failure))
            return Result.Failure<Guid>(failure!);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        try
        {
            await phases.UpdateAsync(current with { EndsOn = command.EffectiveOn }, ct); // its version as read
        }
        catch (PhaseConflictException)
        {
            return Result.Failure<Guid>(OpenSource.Conflict());
        }
        await phases.AddAsync(next, null, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(next.Id);
    }

    private static Result<Guid> Fail(string code, string field, string message)
        => Result.Failure<Guid>(new Failure(code, message, new Dictionary<string, object?> { ["field"] = field }));
}

[RequestHandler<GetIncomeExpectedQuery, IncomeExpected>]
public sealed class GetIncomeExpectedHandler(IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows)
    : IRequestHandler<GetIncomeExpectedQuery, IncomeExpected>
{
    /// <summary>Spec §4: at most two years, <c>to</c> before <c>from</c> plus two years.</summary>
    public const int MaxYears = 2;

    public async ValueTask<Result<IncomeExpected>> HandleAsync(GetIncomeExpectedQuery query, CancellationToken ct = default)
    {
        // The source first: an item that is not a source answers 404 whatever the period.
        var loaded = await SourceCheck.LoadAsync(sources, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<IncomeExpected>(failure!);
        if (query.From > query.To || query.To >= query.From.PlusYears(MaxYears))
            return Result.Failure<IncomeExpected>(new Failure(IncomeCodes.InvalidPeriod,
                $"A period starts on or before its end and covers at most {MaxYears} years.", null));
        loaded.TryGetValue(out var source);
        var currency = SourceCheck.CurrencyOf(source.Item);

        var range = DateRange.Create(query.From, query.To.PlusDays(1)).TryGetValue(out var r) ? r : throw new InvalidOperationException();
        var oneOffs = await GetIncomeSourceHandler.OneOffsAsync(flows, query.ItemId, range, ct);
        if (oneOffs.TryGetFailure(out failure))
            return Result.Failure<IncomeExpected>(failure!);
        oneOffs.TryGetValue(out var listed);
        var months = IncomeMath.Expected(await phases.ForSourceAsync(query.ItemId, ct), listed!, query.From, query.To, currency);

        return Result.Success(new IncomeExpected(query.ItemId, query.From, query.To, months.Select(m => new IncomeExpectedMonth(
            m.Year, m.Month,
            m.Occurrences.Select(o => new IncomeOccurrenceView(o.PhaseId, o.Label, o.On, IncomeMoneyView.From(o.Amount))).ToList(),
            m.OneOffs.Select(IncomeOneOffView.Of).ToList(),
            IncomeMoneyView.From(m.Total))).ToList()));
    }
}
