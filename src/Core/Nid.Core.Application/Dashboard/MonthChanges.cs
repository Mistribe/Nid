using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Dashboard;

/// <summary>
/// Issue #415: one cause of a figure's change, in the household currency. Its key is <c>{module}.paid_in</c> or <c>{module}.value</c>
/// for the net worth (what was paid in, or the capital repaid, and the rest), a <see cref="CommitmentSources"/> code for the
/// commitments, the module for the income.
/// </summary>
public sealed record MonthCauseView(string Key, DashboardMoneyView Change);

/// <summary>Issue #415: a figure a month ago and today, its change, and its largest causes (at most three, the largest first).</summary>
public sealed record MonthFigureView(DashboardMoneyView Before, DashboardMoneyView Now, DashboardMoneyView Change, IReadOnlyList<MonthCauseView> Causes);

/// <summary>
/// Issue #415: « Depuis le mois dernier ». A figure is null when it had nothing a month ago (<c>Since</c>) or a rate is missing;
/// all three null (the first month) hides the block.
/// </summary>
public sealed record MonthChangesView(string Currency, LocalDate Since, MonthFigureView? NetWorth, MonthFigureView? Commitments, MonthFigureView? Income)
{
    public bool IsEmpty => NetWorth is null && Commitments is null && Income is null;
}

public sealed record GetMonthChangesQuery(VisibilityScope Scope) : IQuery<MonthChangesView>;

/// <summary>Issue #415: a part of a figure under its cause's key, a month ago and today, per currency.</summary>
public sealed record MonthPart(string Key, IReadOnlyDictionary<Currency, decimal> Before, IReadOnlyDictionary<Currency, decimal> Now);

public static class MonthChanges
{
    public const int MaxCauses = 3;

    /// <summary>
    /// Sums the parts a month ago at that day's rate and today at today's, as the net worth curve does; a cause is the change of
    /// the parts under its key, kept when it is not zero to the cent. Null when nothing was there a month ago, or a rate is missing.
    /// </summary>
    public static MonthFigureView? Figure(IReadOnlyList<MonthPart> parts, bool hadBefore, Currency reference, RateTable rates, LocalDate since, LocalDate today)
    {
        if (!hadBefore) return null;
        decimal before = 0m, now = 0m;
        var causes = new List<(string Key, decimal Change)>();
        foreach (var group in parts.GroupBy(p => p.Key))
        {
            var b = DashboardMath.Sum.Of(DashboardMath.Merge(group.Select(p => p.Before)), reference, rates, since);
            var n = DashboardMath.Sum.Of(DashboardMath.Merge(group.Select(p => p.Now)), reference, rates, today);
            if (b.Missing is not null || n.Missing is not null) return null;
            before += b.Total ?? 0m;
            now += n.Total ?? 0m;
            causes.Add((group.Key, (n.Total ?? 0m) - (b.Total ?? 0m)));
        }
        return new MonthFigureView(
            DashboardMath.Money(before, reference), DashboardMath.Money(now, reference), DashboardMath.Money(now - before, reference),
            causes.Where(c => Math.Round(c.Change, 2, MidpointRounding.AwayFromZero) != 0m)
                .OrderByDescending(c => Math.Abs(c.Change)).ThenBy(c => c.Key, StringComparer.Ordinal)
                .Take(MaxCauses)
                .Select(c => new MonthCauseView(c.Key, DashboardMath.Money(c.Change, reference)))
                .ToList());
    }

    /// <summary>
    /// A net worth part as its causes: what was paid in (contributions, capital repaid), what was moved by a purchase or a sale
    /// (issue #12, when either point has one), and the rest of its value. They add up to the part's change.
    /// </summary>
    public static IEnumerable<MonthPart> NetWorthParts(string module, NetWorthPoint before, NetWorthPoint now)
    {
        IReadOnlyDictionary<Currency, decimal> none = new Dictionary<Currency, decimal>();
        var movedBefore = before.Moved ?? none;
        var movedNow = now.Moved ?? none;
        yield return new MonthPart($"{module}.paid_in", before.NetContributions, now.NetContributions);
        if (before.Moved is not null || now.Moved is not null)
            yield return new MonthPart($"{module}.moved", movedBefore, movedNow);
        yield return new MonthPart($"{module}.value",
            Minus(Minus(before.Values, before.NetContributions), movedBefore), Minus(Minus(now.Values, now.NetContributions), movedNow));
    }

    internal static bool AnyAmount(IEnumerable<IReadOnlyDictionary<Currency, decimal>> sums) => sums.Any(s => s.Values.Any(v => v != 0m));

    private static IReadOnlyDictionary<Currency, decimal> Minus(IReadOnlyDictionary<Currency, decimal> a, IReadOnlyDictionary<Currency, decimal> b)
        => DashboardMath.Merge([a, b.ToDictionary(p => p.Key, p => -p.Value)]);
}

/// <summary>Issue #415: the month's changes for a reader on a day; the handler serves them, the notification source words them.</summary>
public sealed class MonthChangesReader(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<ICommitmentsContribution> commitments, IEnumerable<IIncomeContribution> income,
    IHouseholdSettings household, IFxRates fx)
{
    public async Task<Result<MonthChangesView>> ReadAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var since = today.PlusMonths(-1);
        var worthSources = netWorth.ToList();
        var incomeSources = income.ToList();

        var worth = await Contributions.CollectAsync(worthSources, c => c.NetWorthHistoryAsync(reader, scope, [since, today], ct));
        if (worth.TryGetFailure(out var failure)) return Result.Failure<MonthChangesView>(failure!);
        var committedThen = await Contributions.CollectAsync(commitments, c => c.CommitmentsAsync(reader, scope, since, ct));
        if (committedThen.TryGetFailure(out failure)) return Result.Failure<MonthChangesView>(failure!);
        var committedNow = await Contributions.CollectAsync(commitments, c => c.CommitmentsAsync(reader, scope, today, ct));
        if (committedNow.TryGetFailure(out failure)) return Result.Failure<MonthChangesView>(failure!);
        var earnedThen = await Contributions.CollectAsync(incomeSources, c => c.IncomeAsync(reader, scope, since, ct));
        if (earnedThen.TryGetFailure(out failure)) return Result.Failure<MonthChangesView>(failure!);
        var earnedNow = await Contributions.CollectAsync(incomeSources, c => c.IncomeAsync(reader, scope, today, ct));
        if (earnedNow.TryGetFailure(out failure)) return Result.Failure<MonthChangesView>(failure!);
        worth.TryGetValue(out var w);
        committedThen.TryGetValue(out var c0);
        committedNow.TryGetValue(out var c1);
        earnedThen.TryGetValue(out var e0);
        earnedNow.TryGetValue(out var e1);

        // Each source's points are [since, today], in the order asked.
        var worthParts = worthSources.Zip(w!).SelectMany(x => MonthChanges.NetWorthParts(x.First.Module, x.Second[0], x.Second[1])).ToList();
        var commitmentParts = c0!.Zip(c1!).Select(x => new MonthPart(x.First.Source, x.First.Amortized, x.Second.Amortized)).ToList();
        var incomeParts = incomeSources.Zip(e0!.Zip(e1!)).Select(x => new MonthPart(x.First.Module, x.Second.First.MonthlyEquivalent, x.Second.Second.MonthlyEquivalent)).ToList();

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = worthParts.Concat(commitmentParts).Concat(incomeParts).SelectMany(p => p.Before.Keys.Concat(p.Now.Keys))
            .Where(c => c != reference).Distinct().ToList();
        // The reference's own quote too: rates are per USD.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), since, today, ct);

        return Result.Success(new MonthChangesView(reference.Code, since,
            MonthChanges.Figure(worthParts, w!.Sum(p => p[0].Envelopes) > 0, reference, rates, since, today),
            MonthChanges.Figure(commitmentParts, MonthChanges.AnyAmount(c0!.Select(p => p.Amortized)), reference, rates, since, today),
            MonthChanges.Figure(incomeParts, MonthChanges.AnyAmount(e0!.Select(p => p.MonthlyEquivalent)), reference, rates, since, today)));
    }
}

[RequestHandler<GetMonthChangesQuery, MonthChangesView>]
public sealed class GetMonthChangesHandler(MonthChangesReader changes, IActorAccessor actor, IHouseholdCalendar calendar)
    : IRequestHandler<GetMonthChangesQuery, MonthChangesView>
{
    public async ValueTask<Result<MonthChangesView>> HandleAsync(GetMonthChangesQuery query, CancellationToken ct = default)
        => await changes.ReadAsync(actor.Current!.PersonId!.Value, query.Scope, await calendar.TodayAsync(ct), ct);
}
