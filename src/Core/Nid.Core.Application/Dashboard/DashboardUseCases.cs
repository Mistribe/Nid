using Nid.Core.Contracts;
using Nid.Core.Domain.Dashboard;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Dashboard;

/// <summary>2D1 spec §4: the home page's figures for the reader, in the Foyer / Moi scope.</summary>
public sealed record GetDashboardQuery(VisibilityScope Scope) : IQuery<DashboardView>;

[RequestHandler<GetDashboardQuery, DashboardView>]
public sealed class GetDashboardHandler(DashboardReader dashboard, IActorAccessor actor, IHouseholdCalendar calendar)
    : IRequestHandler<GetDashboardQuery, DashboardView>
{
    public async ValueTask<Result<DashboardView>> HandleAsync(GetDashboardQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        return await dashboard.ReadAsync(reader, query.Scope, today, ct);
    }
}

/// <summary>The dashboard's composition for a reader on a day: the handler serves it, the notification source reads the same figures.</summary>
public sealed class DashboardReader(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<ICommitmentsContribution> commitments,
    IEnumerable<IIncomeContribution> income, IEnumerable<ISpendingContribution> spending, IEnumerable<IUpcomingEvents> upcoming, IHouseholdTax tax,
    IHouseholdSettings household, IFxRates fx, IPersonRegistry persons) : IMonthBudget
{
    /// <summary><paramref name="withSpending"/> false leaves the everyday spending out (null), as the notifications never use it.</summary>
    public async Task<Result<DashboardView>> ReadAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct, bool withSpending = true)
    {
        var worth = await Contributions.CollectAsync(netWorth, c => c.NetWorthAsync(reader, scope, today, ct));
        if (worth.TryGetFailure(out var failure)) return Result.Failure<DashboardView>(failure!);
        var committed = await Contributions.CollectAsync(commitments, c => c.CommitmentsAsync(reader, scope, today, ct));
        if (committed.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        var earned = await Contributions.CollectAsync(income, c => c.IncomeAsync(reader, scope, today, ct));
        if (earned.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        var spent = await Contributions.CollectAsync(withSpending ? spending : [], s => s.EverydayAsync(reader, scope, today, ct));
        if (spent.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        var until = today.PlusDays(DashboardMath.UpcomingDays);
        var dated = await Contributions.CollectAsync(upcoming, u => u.UpcomingAsync(reader, scope, today, until, ct));
        if (dated.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        var per = await tax.PerDeadlineAsync(reader, scope, today, until, ct);
        if (per.TryGetFailure(out failure)) return Result.Failure<DashboardView>(failure!);
        worth.TryGetValue(out var w);
        committed.TryGetValue(out var c);
        earned.TryGetValue(out var e);
        spent.TryGetValue(out var s);
        dated.TryGetValue(out var d);
        per.TryGetValue(out var p);
        var taxDates = await tax.CalendarAsync(reader, scope, today, until, ct);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = DashboardMath.Currencies(w!, c!, e!, s!).Where(x => x != reference).ToList();
        // The reference's own quote is needed too: rates are per USD (RateTable converts through it).
        var rates = foreign.Count == 0
            ? RateTable.Empty(today, 0)
            : await fx.LoadAsync(foreign.Append(reference).ToList(), today, today, ct);
        var people = await persons.ListAsync(ct);
        var target = await household.AllocationTargetAsync(ct);
        return Result.Success(DashboardComposition.Compose(w!, c!, e!, s!, d!.SelectMany(x => x).ToList(), people, reference, rates, today, target, [.. p!, .. taxDates]));
    }

    /// <summary>Issue #425: the parts <see cref="ReadAsync"/> reads, through the same <see cref="DashboardMath.Budget"/>.</summary>
    public async Task<Result<MonthBudget>> MonthAsync(PersonId reader, VisibilityScope scope, YearMonth month, LocalDate today, CancellationToken ct)
    {
        var day = month == today.ToYearMonth() ? today : month.ToDateInterval().End;
        var committed = await Contributions.CollectAsync(commitments, c => c.CommitmentsAsync(reader, scope, day, ct, includeArchived: month < today.ToYearMonth()));
        if (committed.TryGetFailure(out var failure)) return Result.Failure<MonthBudget>(failure!);
        var earned = await Contributions.CollectAsync(income, c => c.IncomeAsync(reader, scope, day, ct));
        if (earned.TryGetFailure(out failure)) return Result.Failure<MonthBudget>(failure!);
        var spent = await Contributions.CollectAsync(spending, s => s.EverydayAsync(reader, scope, day, ct));
        if (spent.TryGetFailure(out failure)) return Result.Failure<MonthBudget>(failure!);
        committed.TryGetValue(out var c);
        earned.TryGetValue(out var e);
        spent.TryGetValue(out var s);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = DashboardMath.Currencies([], c!, e!, s!).Where(x => x != reference).ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(day, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), day, day, ct);
        return Result.Success(DashboardMath.Budget(c!, e!, s!, reference, rates, day));
    }

    /// <summary>
    /// Issue #80: « À venir » alone over [<paramref name="today"/>, <paramref name="until"/>], for the calendar feed: the modules' events
    /// (not the income gap, which is « À faire »), the PER deadline and the tax calendar, by date then name, each amount in its own currency.
    /// Issue #243: only the events of <paramref name="modules"/> (<see cref="CalendarModules"/>), every module when null.
    /// Issue #240: <paramref name="milestonesOnly"/> keeps the kinds that happen once (<see cref="UpcomingKinds.IsMilestone"/>).
    /// </summary>
    public async Task<Result<IReadOnlyList<DashboardEventView>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until,
        IReadOnlyCollection<string>? modules, CancellationToken ct, bool milestonesOnly = false)
    {
        bool Lists(string module) => modules is null || modules.Contains(module);
        var dated = await Contributions.CollectAsync(upcoming, u => u.UpcomingAsync(reader, scope, today, until, ct));
        if (dated.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<DashboardEventView>>(failure!);
        dated.TryGetValue(out var d);
        IReadOnlyList<DashboardEventView> p = [], taxDates = [];
        // The PER deadline computes the year's tax and the calendar reads the notices: neither when the tax is not listed, nor for
        // milestones, since both only date recurring kinds.
        if (Lists(CalendarModules.Tax) && !milestonesOnly)
        {
            var per = await tax.PerDeadlineAsync(reader, scope, today, until, ct);
            if (per.TryGetFailure(out failure)) return Result.Failure<IReadOnlyList<DashboardEventView>>(failure!);
            per.TryGetValue(out var perEvents);
            p = perEvents!;
            taxDates = await tax.CalendarAsync(reader, scope, today, until, ct);
        }
        return Result.Success<IReadOnlyList<DashboardEventView>>(d!.SelectMany(x => x)
            .Where(e => e.Kind != UpcomingKinds.IncomeGapSoon && Lists(e.Module) && (!milestonesOnly || UpcomingKinds.IsMilestone(e.Kind)))
            .Select(e => new DashboardEventView(e.On, e.Kind, e.Item, e.Module, e.Name, e.Label,
                e.Amount is { } a ? DashboardMath.Money(a.Amount, a.Currency) : null))
            .Concat(p).Concat(taxDates)
            .OrderBy(e => e.On).ThenBy(e => e.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), true))
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToList());
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
