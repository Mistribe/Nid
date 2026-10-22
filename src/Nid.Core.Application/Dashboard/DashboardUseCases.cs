using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Dashboard;

/// <summary>2D1 spec §4: the home page's figures for the reader, in the Foyer / Moi scope.</summary>
public sealed record GetDashboardQuery(VisibilityScope Scope) : IQuery<DashboardView>;

[RequestHandler<GetDashboardQuery, DashboardView>]
public sealed class GetDashboardHandler(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<ICommitmentsContribution> commitments,
    IEnumerable<IIncomeContribution> income, IEnumerable<IUpcomingEvents> upcoming, IActorAccessor actor, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx, IPersonRegistry persons)
    : IRequestHandler<GetDashboardQuery, DashboardView>
{
    public async ValueTask<Result<DashboardView>> HandleAsync(GetDashboardQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);

        var worth = await Contributions.CollectAsync(netWorth, c => c.NetWorthAsync(reader, query.Scope, today, ct));
        if (worth.TryGetFailure(out var failure)) return Result.Failure<DashboardView>(failure!);
        var committed = await Contributions.CollectAsync(commitments, c => c.CommitmentsAsync(reader, query.Scope, today, ct));
        if (committed.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        var earned = await Contributions.CollectAsync(income, c => c.IncomeAsync(reader, query.Scope, today, ct));
        if (earned.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        var until = today.PlusDays(DashboardMath.UpcomingDays);
        var dated = await Contributions.CollectAsync(upcoming, u => u.UpcomingAsync(reader, query.Scope, today, until, ct));
        if (dated.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        worth.TryGetValue(out var w);
        committed.TryGetValue(out var c);
        earned.TryGetValue(out var e);
        dated.TryGetValue(out var d);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = DashboardMath.Currencies(w!, c!, e!).Where(x => x != reference).ToList();
        // The reference's own quote is needed too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0
            ? RateTable.Empty(today, 0)
            : await fx.LoadAsync(foreign.Append(reference).ToList(), today, today, ct);
        var people = await persons.ListAsync(ct);
        var target = await household.AllocationTargetAsync(ct);
        return Result.Success(DashboardMath.Compose(w!, c!, e!, d!.SelectMany(x => x).ToList(), people, reference, rates, today, target));
    }
}

/// <summary>Issue #104: replaces the household's target allocation; an empty one removes it.</summary>
public sealed record SaveAllocationTargetCommand(IReadOnlyDictionary<string, decimal> Target) : ICommand<IReadOnlyDictionary<string, decimal>>;

public static class AllocationTarget
{
    public const string Invalid = "core.invalid_allocation_target";

    /// <summary>Known classes only, each from 0 to 100 % to 0.1, adding up to 100 %; or none at all.</summary>
    public static Failure? Check(IReadOnlyDictionary<string, decimal> target)
    {
        if (target.Count == 0) return null;
        if (target.Keys.Any(k => !AllocationClasses.All.Contains(k)))
            return new Failure(Invalid, $"A target names asset classes among: {string.Join(", ", AllocationClasses.All)}.", null);
        if (target.Values.Any(v => v is < 0m or > 100m || decimal.Round(v, 1) != v))
            return new Failure(Invalid, "Each share is from 0 to 100 %, to 0.1 %.", null);
        return target.Values.Sum() == 100m ? null : new Failure(Invalid, "The shares add up to 100 %.", null);
    }
}

[RequestHandler<SaveAllocationTargetCommand, IReadOnlyDictionary<string, decimal>>]
public sealed class SaveAllocationTargetHandler(IHouseholdSettings household)
    : IRequestHandler<SaveAllocationTargetCommand, IReadOnlyDictionary<string, decimal>>
{
    public async ValueTask<Result<IReadOnlyDictionary<string, decimal>>> HandleAsync(SaveAllocationTargetCommand command, CancellationToken ct = default)
    {
        if (AllocationTarget.Check(command.Target) is { } failure)
            return Result.Failure<IReadOnlyDictionary<string, decimal>>(failure);
        // Zero shares carry nothing: kept out, so the dashboard lists only the classes aimed at.
        var target = command.Target.Where(t => t.Value > 0m).ToDictionary(t => t.Key, t => t.Value);
        await household.SaveAllocationTargetAsync(target, ct);
        return Result.Success<IReadOnlyDictionary<string, decimal>>(target);
    }
}
