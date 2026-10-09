using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
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
        return Result.Success(DashboardMath.Compose(w!, c!, e!, d!.SelectMany(x => x).ToList(), people, reference, rates, today));
    }
}
