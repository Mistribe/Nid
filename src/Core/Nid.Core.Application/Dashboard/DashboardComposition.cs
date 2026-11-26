using System.Globalization;
using Nid.Core.Contracts;
using Nid.Core.Domain.Dashboard;
using NodaTime;
using static Nid.Core.Domain.Dashboard.DashboardMath;

namespace Nid.Core.Application.Dashboard;

/// <summary>A figure's per-currency sums and their total in the household currency: null with nothing to sum, or when a rate is missing (then <c>MissingCurrency</c> names it).</summary>
public sealed record DashboardFigure(IReadOnlyList<DashboardMoneyView> PerCurrency, DashboardMoneyView? Total, string? MissingCurrency);

/// <summary>
/// 2D2a spec §4: one envelope of the split; <c>Converted</c> null without a value or with a missing rate (then <c>MissingCurrency</c> names it).
/// Holding prices spec §5.4: <c>Estimated</c> when the value is the envelope's estimate, dated <c>AsOf</c>; <c>ValuedOn</c> is the latest
/// valuation's date, the one <c>Stale</c> speaks of (D17).
/// </summary>
public sealed record DashboardEnvelopeView(
    ItemId Id, string Module, string Name, DashboardMoneyView? Value, DashboardMoneyView? Converted, string? MissingCurrency,
    LocalDate? AsOf, bool Stale, bool Estimated, LocalDate? ValuedOn);

/// <summary>2D2a spec §4: a line of « À faire »: <c>stale_value</c> (dated by the latest valuation, not the estimate), <c>no_value</c>, <c>income_gap_soon</c> (dated by the end).</summary>
public sealed record DashboardTodoView(string Kind, ItemId Id, string Module, string Name, LocalDate? On, string? Label);

/// <summary>2D2a spec §4: a person's pro-rata share. <c>Slot</c> is their colour rank: adults by creation, then dependents.</summary>
public sealed record DashboardPersonView(Guid Id, string Name, int Slot, DashboardMoneyView Amount, decimal Pct);

/// <summary>
/// 3E1 spec §3: one source of the commitments, its amortized month in the household currency; null when a rate is missing.
/// <c>Pct</c> is its share of the month's income, null without an income above 0 or without its amount.
/// </summary>
public sealed record DashboardCommitmentSourceView(string Source, DashboardMoneyView? Amortized, decimal? Pct, bool Estimated = false);

/// <summary>
/// Issue #104: an asset class or a liquidity of the allocation, its share of the assets (0.1 %). With a target for the class:
/// <c>GapPct</c> its share minus its target, and <c>Gap</c> the amount above (+) or below (−) the target.
/// </summary>
public sealed record AllocationLineView(string Key, DashboardMoneyView Amount, decimal Pct, decimal? TargetPct, decimal? GapPct, DashboardMoneyView? Gap);

/// <summary>
/// Issue #104: the net worth by asset class and by liquidity. <c>Assets</c> + <c>Debts</c> (negative) is the net worth;
/// <c>Approximate</c> is the part of the assets whose class is guessed from a kind; <c>Target</c> the household's, in %.
/// </summary>
public sealed record AllocationView(
    DashboardMoneyView Assets, DashboardMoneyView Debts, DashboardMoneyView Approximate,
    IReadOnlyList<AllocationLineView> Classes, IReadOnlyList<AllocationLineView> Liquidity, IReadOnlyDictionary<string, decimal> Target);

/// <summary>
/// #85 decision 10: a top-level category's everyday spending this month, in the household currency. <c>Others</c> marks the slice
/// that gathers the rest (named « Autres »), <c>Uncategorised</c> the lines without a category (« Non classées »), so a category of
/// either name is never taken for them.
/// </summary>
public sealed record DashboardSpendingSliceView(string Name, DashboardMoneyView Amount, bool Others, bool Uncategorised);

public sealed record DashboardView(
    string Currency,
    DashboardFigure NetWorth, LocalDate? ValuesAsOf, int Envelopes, int EnvelopesWithoutValue, int StaleEnvelopes, int EstimatedEnvelopes,
    DashboardFigure Commitments, DashboardFigure CashThisMonth, int Subscriptions,
    DashboardFigure Income, int IncomeSources, DashboardMoneyView? Margin, decimal? CommittedPct, decimal? MarginPct,
    DashboardFigure Change, DashboardFigure ChangeContributions, decimal? ChangePct,
    IReadOnlyList<DashboardEnvelopeView> EnvelopeSplit, IReadOnlyList<DashboardPersonView>? People, string? PeopleMissingCurrency,
    IReadOnlyList<DashboardEventView> Upcoming, IReadOnlyList<DashboardTodoView> Todo, int EnvelopesWithoutStart,
    IReadOnlyList<DashboardCommitmentSourceView> CommitmentSources,
    // Issue #104: null without assets above 0, or when a rate is missing (then PeopleMissingCurrency names it, the split's).
    AllocationView? Allocation,
    // #85 decision 10: null without a bank account or with bank sync off, or when a rate is missing; Left (margin − everyday spending)
    // also without a margin. The slices: the five largest top-level categories, then « Autres »; none without the figure. Refunds
    // net within a category, so everyday spending can be below zero and the positive slices can add up to more than it.
    DashboardMoneyView? EverydaySpending, DashboardMoneyView? Left, IReadOnlyList<DashboardSpendingSliceView> SpendingSlices,
    // #720: the cards attached to no account, with a line this month, that the spending figure leaves out.
    IReadOnlyList<SpendingCard> UnattachedCards)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => EnvelopeSplit.Select(e => e.Id).Concat(Upcoming.Select(e => e.Id).OfType<ItemId>()).Concat(Todo.Select(t => t.Id));
}

/// <summary>
/// 2D1 spec §4: the dashboard's view from the modules' parts, on <see cref="DashboardMath"/>'s sums: the margin, the share
/// committed and the change percent; 2D2a: the split by envelope and by person, from the holdings converted at today's rate.
/// </summary>
public static class DashboardComposition
{
    public const int SpendingSlices = 5;
    public const string OtherSpending = "Autres";

    /// <summary>Names sort in French order, ignoring case, as the envelope list does.</summary>
    private static readonly StringComparer French = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), ignoreCase: true);

    public static DashboardView Compose(
        IReadOnlyList<NetWorthPart> worth, IReadOnlyList<CommitmentsPart> commitments, IReadOnlyList<IncomePart> income,
        IReadOnlyList<SpendingPart> spending, IReadOnlyList<UpcomingEvent> events, IReadOnlyList<PersonInfo> persons, Currency reference, RateTable rates, LocalDate today,
        IReadOnlyDictionary<string, decimal>? target = null, IReadOnlyList<DashboardEventView>? itemless = null)
    {
        Sum Of(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts) => Sum.Of(Merge(parts), reference, rates, today);

        var netWorth = Of(worth.Select(p => p.Values));
        var start = Of(worth.Select(p => p.StartValues));
        var net = Of(worth.Select(p => p.NetContributions));
        var gain = Of(worth.Select(p => p.Gains));
        var amortized = Of(commitments.Select(p => p.Amortized));
        var cash = Of(commitments.Select(p => p.Cash));
        var monthly = Of(income.Select(p => p.MonthlyEquivalent));
        var sources = income.Sum(p => p.Sources);
        var budget = Budget(commitments, income, spending, reference, rates, today);
        var (incomeTotal, committed, margin, left) = (budget.Income, budget.Committed, budget.Margin, budget.Left);
        decimal? ShareOfIncome(decimal? part) => incomeTotal is { } i && i > 0m && part is { } p ? Percent(p / i) : null;
        decimal? share = ShareOfIncome(committed);
        List<DashboardSpendingSliceView> slices = [];
        if (budget.Everyday is { } spent)
        {
            // By category, never by name: two categories of one name stay apart.
            var ranked = spending.SelectMany(p => p.ByCategory).GroupBy(s => s.Category)
                .Select(g => (g.First().Name, Uncategorised: g.Key is null, Amount: Of(g.Select(s => s.Amount)).Total ?? 0m))
                .Where(s => s.Amount > 0m)
                .OrderByDescending(s => s.Amount).ThenBy(s => s.Name, French)
                .Take(SpendingSlices).ToList();
            slices = [.. ranked.Select(s => new DashboardSpendingSliceView(s.Name, Money(s.Amount, reference), false, s.Uncategorised))];
            // « Autres » is the rest of the total, so the slices add up to it; a net refund among the rest can leave nothing to show,
            // and one larger than the top five's spending leaves them above the total.
            var rest = spent - ranked.Sum(s => s.Amount);
            if (Math.Round(rest, 2, MidpointRounding.AwayFromZero) > 0m)
                slices.Add(new DashboardSpendingSliceView(OtherSpending, Money(rest, reference), true, false));
        }

        // 3E1 spec §3: each source counting at least one item, its amortized month converted as the total.
        var bySource = commitments.GroupBy(p => p.Source)
            .Where(g => g.Sum(p => p.Count) > 0)
            .OrderBy(g => SourceRank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var sum = Of(g.Select(p => p.Amortized));
                decimal? total = sum.Missing is null ? sum.Total ?? 0m : null;
                return new DashboardCommitmentSourceView(g.Key, total is { } t ? Money(t, reference) : null, ShareOfIncome(total), g.Any(p => p.Estimated));
            })
            .ToList();
        decimal? changePct = gain.Total is { } g && start.Total is { } s && net.Total is { } n && s + n > 0m ? Percent(g / (s + n)) : null;

        var holdings = worth.SelectMany(p => p.Holdings)
            .Select(h => (Holding: h, Converted: Convert(h.Value, reference, rates, today)))
            .ToList();
        var split = holdings
            .OrderBy(h => h.Holding.Value is null ? 2 : h.Converted.Amount is null ? 1 : 0)
            .ThenByDescending(h => h.Converted.Amount ?? 0m)
            .ThenBy(h => h.Holding.Name, French)
            .Select(h => new DashboardEnvelopeView(
                h.Holding.Item, h.Holding.Module, h.Holding.Name,
                h.Holding.Value is { } v ? Money(v.Amount, v.Currency) : null,
                h.Converted.Amount is { } c ? Money(c, reference) : null,
                h.Converted.Missing, h.Holding.AsOf, h.Holding.Stale, h.Holding.Estimated, h.Holding.ValuedOn ?? h.Holding.AsOf))
            .ToList();
        var peopleMissing = holdings.Select(h => h.Converted.Missing).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();
        var people = peopleMissing is null ? People(holdings.Select(h => (h.Holding.Owners, h.Converted.Amount)), persons, reference) : null;

        var upcoming = events.Where(e => !UpcomingKinds.IsTodo(e.Kind))
            .Select(e => new DashboardEventView(e.On, e.Kind, e.Item, e.Module, e.Name, e.Label,
                e.Amount is { } a ? Money(a.Amount, a.Currency) : null))
            .Concat(itemless ?? [])
            .OrderBy(e => e.On).ThenBy(e => e.Name, French).ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToList();
        var todo = holdings.Where(h => h.Holding is { Value: not null, Stale: true })
            .OrderBy(h => h.Holding.ValuedOn ?? h.Holding.AsOf).ThenBy(h => h.Holding.Name, French)
            .Select(h => new DashboardTodoView("stale_value", h.Holding.Item, h.Holding.Module, h.Holding.Name, h.Holding.ValuedOn ?? h.Holding.AsOf, null))
            .Concat(holdings.Where(h => h.Holding.Value is null).OrderBy(h => h.Holding.Name, French)
                .Select(h => new DashboardTodoView("no_value", h.Holding.Item, h.Holding.Module, h.Holding.Name, null, null)))
            .Concat(events.Where(e => UpcomingKinds.IsTodo(e.Kind)).OrderBy(e => e.On).ThenBy(e => e.Name, French)
                .Select(e => new DashboardTodoView(e.Kind, e.Item, e.Module, e.Name, e.On, e.Label)))
            .ToList();

        return new DashboardView(
            reference.Code,
            netWorth.View(reference), worth.Select(p => p.OldestAsOf).Min(), worth.Sum(p => p.Envelopes),
            worth.Sum(p => p.WithoutValue), worth.Sum(p => p.Stale), holdings.Count(h => h.Holding.Estimated),
            amortized.View(reference), cash.View(reference),
            commitments.Where(p => p.Source == CommitmentSources.Subscriptions).Sum(p => p.Count),
            sources > 0 ? monthly.View(reference) : Sum.Nothing.View(reference), sources,
            margin is { } m ? Money(m, reference) : null, share, ShareOfIncome(margin),
            gain.View(reference), net.View(reference), changePct,
            split, people, peopleMissing, upcoming, todo, worth.Sum(p => p.WithoutStart), bySource,
            peopleMissing is null ? ViewOf(Allocation(holdings, target ?? new Dictionary<string, decimal>()), reference) : null,
            budget.Everyday is { } e ? Money(e, reference) : null, left is { } l ? Money(l, reference) : null, slices,
            [.. spending.SelectMany(p => p.UnattachedCards ?? [])]);
    }

    /// <summary>Spec decision 2: value × share, pro rata; a person at zero is left out; percent of the persons' total.</summary>
    private static List<DashboardPersonView> People(
        IEnumerable<(IReadOnlyList<OwnerShare> Owners, decimal? Amount)> holdings, IReadOnlyList<PersonInfo> persons, Currency reference)
    {
        var shares = new Dictionary<PersonId, decimal>();
        foreach (var (owners, amount) in holdings)
            if (amount is { } a)
                foreach (var owner in owners)
                    shares[owner.PersonId] = shares.GetValueOrDefault(owner.PersonId) + a * owner.ShareBp / 10_000m;
        var total = shares.Values.Sum();
        // OrderBy is stable: ListAsync gives creation order, so adults keep theirs, then dependents keep theirs.
        return persons.OrderBy(p => p.Kind == PersonKind.Adult ? 0 : 1)
            .Select((p, slot) => (Person: p, Slot: slot, Amount: shares.GetValueOrDefault(p.Id)))
            .Where(x => x.Amount != 0m)
            .OrderByDescending(x => x.Amount).ThenBy(x => x.Person.DisplayName, French)
            .Select(x => new DashboardPersonView(x.Person.Id.Value, x.Person.DisplayName, x.Slot, Money(x.Amount, reference),
                total > 0m ? Percent(x.Amount / total) : 0m))
            .ToList();
    }


    /// <summary>Issue #104: the allocation's figures on the wire; null without assets above 0.</summary>
    internal static AllocationView? ViewOf(AllocationFigures? allocation, Currency reference)
    {
        if (allocation is null) return null;
        AllocationLineView Line(AllocationLine l)
            => new(l.Key, Money(l.Amount, reference), l.Pct, l.TargetPct, l.GapPct, l.Gap is { } gap ? Money(gap, reference) : null);
        return new AllocationView(Money(allocation.Assets, reference), Money(allocation.Debts, reference), Money(allocation.Approximate, reference),
            allocation.Classes.Select(Line).ToList(), allocation.Liquidity.Select(Line).ToList(), allocation.Target);
    }

    /// <summary>A sum's figure: its per-currency sums and its total, in the household currency.</summary>
    public static DashboardFigure View(this DashboardMath.Sum sum, Currency reference) => new(
        sum.PerCurrency.OrderBy(s => s.Key.Code, StringComparer.Ordinal).Select(s => Money(s.Value, s.Key)).ToList(),
        sum.Total is { } t ? Money(t, reference) : null,
        sum.Missing);
}
