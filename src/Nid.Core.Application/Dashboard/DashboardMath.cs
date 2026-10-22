using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Dashboard;

public sealed record DashboardMoneyView(string Amount, string Currency);

/// <summary>A figure's per-currency sums and their total in the household currency: null with nothing to sum, or when a rate is missing (then <c>MissingCurrency</c> names it).</summary>
public sealed record DashboardFigure(IReadOnlyList<DashboardMoneyView> PerCurrency, DashboardMoneyView? Total, string? MissingCurrency);

/// <summary>2D2a spec §4: one envelope of the split; <c>Converted</c> null without a value or with a missing rate (then <c>MissingCurrency</c> names it).</summary>
public sealed record DashboardEnvelopeView(
    ItemId Id, string Module, string Name, DashboardMoneyView? Value, DashboardMoneyView? Converted, string? MissingCurrency,
    LocalDate? AsOf, bool Stale);

/// <summary>2D2a spec §4: an event of « À venir »; the amount in its own currency.</summary>
public sealed record DashboardEventView(LocalDate On, string Kind, ItemId Id, string Module, string Name, string? Label, DashboardMoneyView? Amount);

/// <summary>2D2a spec §4: a line of « À faire »: <c>stale_value</c> (dated by the last value), <c>no_value</c>, <c>income_gap_soon</c> (dated by the end).</summary>
public sealed record DashboardTodoView(string Kind, ItemId Id, string Module, string Name, LocalDate? On, string? Label);

/// <summary>2D2a spec §4: a person's pro-rata share. <c>Slot</c> is their colour rank: adults by creation, then dependents.</summary>
public sealed record DashboardPersonView(Guid Id, string Name, int Slot, DashboardMoneyView Amount, decimal Pct);

/// <summary>
/// 3E1 spec §3: one source of the commitments, its amortized month in the household currency; null when a rate is missing.
/// <c>Pct</c> is its share of the month's income, null without an income above 0 or without its amount.
/// </summary>
public sealed record DashboardCommitmentSourceView(string Source, DashboardMoneyView? Amortized, decimal? Pct);

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

public sealed record DashboardView(
    string Currency,
    DashboardFigure NetWorth, LocalDate? ValuesAsOf, int Envelopes, int EnvelopesWithoutValue, int StaleEnvelopes,
    DashboardFigure Commitments, DashboardFigure CashThisMonth, int Subscriptions,
    DashboardFigure Income, int IncomeSources, DashboardMoneyView? Margin, decimal? CommittedPct, decimal? MarginPct,
    DashboardFigure Change, DashboardFigure ChangeContributions, decimal? ChangePct,
    IReadOnlyList<DashboardEnvelopeView> EnvelopeSplit, IReadOnlyList<DashboardPersonView>? People, string? PeopleMissingCurrency,
    IReadOnlyList<DashboardEventView> Upcoming, IReadOnlyList<DashboardTodoView> Todo, int EnvelopesWithoutStart,
    IReadOnlyList<DashboardCommitmentSourceView> CommitmentSources,
    // Issue #104: null without assets above 0, or when a rate is missing (then PeopleMissingCurrency names it, the split's).
    AllocationView? Allocation)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => EnvelopeSplit.Select(e => e.Id).Concat(Upcoming.Select(e => e.Id)).Concat(Todo.Select(t => t.Id));
}

/// <summary>
/// 2D1 spec §4: sums the modules' parts per currency, converts each sum at today's rate, and derives the margin, the share
/// committed and the change percent. Exact decimals throughout; rounded only on the wire.
/// 2D2a: the split by envelope and by person, from the holdings converted at today's rate.
/// </summary>
public static class DashboardMath
{
    public const int UpcomingDays = 60;

    /// <summary>Names sort in French order, ignoring case, as the envelope list does.</summary>
    private static readonly StringComparer French = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), ignoreCase: true);

    public static IReadOnlySet<Currency> Currencies(
        IEnumerable<NetWorthPart> worth, IEnumerable<CommitmentsPart> commitments, IEnumerable<IncomePart> income)
        => worth.SelectMany(p => p.Values.Keys.Concat(p.StartValues.Keys).Concat(p.NetContributions.Keys).Concat(p.Gains.Keys))
            .Concat(commitments.SelectMany(p => p.Amortized.Keys.Concat(p.Cash.Keys)))
            .Concat(income.SelectMany(p => p.MonthlyEquivalent.Keys))
            .ToHashSet();

    public static DashboardView Compose(
        IReadOnlyList<NetWorthPart> worth, IReadOnlyList<CommitmentsPart> commitments, IReadOnlyList<IncomePart> income,
        IReadOnlyList<UpcomingEvent> events, IReadOnlyList<PersonInfo> persons, Currency reference, RateTable rates, LocalDate today,
        IReadOnlyDictionary<string, decimal>? target = null)
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
        // No income declared reads « inconnu », never 0 (spec §3); no subscription commits nothing.
        decimal? incomeTotal = sources > 0 ? monthly.Total : null;
        decimal? ShareOfIncome(decimal? part) => incomeTotal is { } i && i > 0m && part is { } p ? Percent(p / i) : null;
        decimal? committed = amortized.Missing is null ? amortized.Total ?? 0m : null;
        decimal? margin = incomeTotal is { } i && committed is { } c ? i - c : null;
        decimal? share = ShareOfIncome(committed);

        // 3E1 spec §3: each source counting at least one item, its amortized month converted as the total.
        var bySource = commitments.GroupBy(p => p.Source)
            .Where(g => g.Sum(p => p.Count) > 0)
            .OrderBy(g => SourceRank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var sum = Of(g.Select(p => p.Amortized));
                decimal? total = sum.Missing is null ? sum.Total ?? 0m : null;
                return new DashboardCommitmentSourceView(g.Key, total is { } t ? Money(t, reference) : null, ShareOfIncome(total));
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
                h.Converted.Missing, h.Holding.AsOf, h.Holding.Stale))
            .ToList();
        var peopleMissing = holdings.Select(h => h.Converted.Missing).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();
        var people = peopleMissing is null ? People(holdings.Select(h => (h.Holding.Owners, h.Converted.Amount)), persons, reference) : null;

        var upcoming = events.Where(e => e.Kind != UpcomingKinds.IncomeGapSoon)
            .OrderBy(e => e.On).ThenBy(e => e.Name, French).ThenBy(e => e.Kind, StringComparer.Ordinal)
            .Select(e => new DashboardEventView(e.On, e.Kind, e.Item, e.Module, e.Name, e.Label,
                e.Amount is { } a ? Money(a.Amount, a.Currency) : null))
            .ToList();
        var todo = holdings.Where(h => h.Holding is { Value: not null, Stale: true })
            .OrderBy(h => h.Holding.AsOf).ThenBy(h => h.Holding.Name, French)
            .Select(h => new DashboardTodoView("stale_value", h.Holding.Item, h.Holding.Module, h.Holding.Name, h.Holding.AsOf, null))
            .Concat(holdings.Where(h => h.Holding.Value is null).OrderBy(h => h.Holding.Name, French)
                .Select(h => new DashboardTodoView("no_value", h.Holding.Item, h.Holding.Module, h.Holding.Name, null, null)))
            .Concat(events.Where(e => e.Kind == UpcomingKinds.IncomeGapSoon).OrderBy(e => e.On).ThenBy(e => e.Name, French)
                .Select(e => new DashboardTodoView(UpcomingKinds.IncomeGapSoon, e.Item, e.Module, e.Name, e.On, e.Label)))
            .ToList();

        return new DashboardView(
            reference.Code,
            netWorth.View(reference), worth.Select(p => p.OldestAsOf).Min(), worth.Sum(p => p.Envelopes),
            worth.Sum(p => p.WithoutValue), worth.Sum(p => p.Stale),
            amortized.View(reference), cash.View(reference),
            commitments.Where(p => p.Source == CommitmentSources.Subscriptions).Sum(p => p.Count),
            sources > 0 ? monthly.View(reference) : Sum.Nothing.View(reference), sources,
            margin is { } m ? Money(m, reference) : null, share, ShareOfIncome(margin),
            gain.View(reference), net.View(reference), changePct,
            split, people, peopleMissing, upcoming, todo, worth.Sum(p => p.WithoutStart), bySource,
            peopleMissing is null ? Allocation(holdings, reference, target ?? new Dictionary<string, decimal>()) : null);
    }

    /// <summary>
    /// Issue #104: each holding's slices at its own converted rate; a holding without slices is classed by its module (a property
    /// is real estate, locked; a loan is debt). Null without assets above 0.
    /// </summary>
    internal static AllocationView? Allocation(
        IEnumerable<(Holding Holding, (decimal? Amount, string? Missing) Converted)> holdings, Currency reference, IReadOnlyDictionary<string, decimal> target)
    {
        var classes = new Dictionary<string, decimal>();
        var liquidity = new Dictionary<string, decimal>();
        decimal debts = 0m, approximate = 0m;
        foreach (var (holding, converted) in holdings)
        {
            if (holding.Value is not { } value || converted.Amount is not { } amount || value.Amount == 0m) continue;
            if (holding.Module == "loans")
            {
                debts += amount;
                continue;
            }
            var slices = holding.Slices ?? (holding.Module == "properties"
                ? [new AllocationSlice(AllocationClasses.RealEstate, AllocationLiquidity.Locked, value.Amount, false)]
                : [new AllocationSlice(AllocationClasses.Other, AllocationLiquidity.Days, value.Amount, true)]);
            foreach (var slice in slices)
            {
                var part = slice.Amount * amount / value.Amount; // the holding's own rate, as its converted value
                classes[slice.Class] = classes.GetValueOrDefault(slice.Class) + part;
                liquidity[slice.Liquidity] = liquidity.GetValueOrDefault(slice.Liquidity) + part;
                if (slice.Approximate) approximate += part;
            }
        }
        var assets = classes.Values.Sum();
        if (assets <= 0m) return null;

        AllocationLineView Line(string key, decimal amount, decimal? goal)
        {
            var share = amount / assets * 100m;
            return new AllocationLineView(key, Money(amount, reference), Math.Round(share, 1, MidpointRounding.AwayFromZero), goal,
                goal is { } g ? Math.Round(share - g, 1, MidpointRounding.AwayFromZero) : null,
                goal is { } t ? Money(amount - assets * t / 100m, reference) : null);
        }
        return new AllocationView(Money(assets, reference), Money(debts, reference), Money(approximate, reference),
            AllocationClasses.All.Where(c => classes.GetValueOrDefault(c) != 0m || target.ContainsKey(c))
                .Select(c => Line(c, classes.GetValueOrDefault(c), target.TryGetValue(c, out var t) ? t : null)).ToList(),
            AllocationLiquidity.All.Where(l => liquidity.GetValueOrDefault(l) != 0m).Select(l => Line(l, liquidity[l], null)).ToList(),
            target);
    }

    /// <summary>One holding at today's rate: zero converts to zero at any rate, as <see cref="Sum.Of"/>.</summary>
    private static (decimal? Amount, string? Missing) Convert(Money? value, Currency reference, RateTable rates, LocalDate today)
    {
        if (value is not { } v) return (null, null);
        if (v.Amount == 0m) return (0m, null);
        return rates.TryConvert(v.Amount, v.Currency, reference, today, projected: false, out var converted, out _)
            ? (converted, null)
            : (null, v.Currency.Code);
    }

    /// <summary>3E1 spec §3: « Abonnements · Crédits · Charges des biens ».</summary>
    private static int SourceRank(string source) => source switch
    {
        CommitmentSources.Subscriptions => 0,
        CommitmentSources.Loans => 1,
        CommitmentSources.PropertyCosts => 2,
        _ => 3,
    };

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

    internal static IReadOnlyDictionary<Currency, decimal> Merge(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts)
        => parts.SelectMany(p => p).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

    private static decimal Percent(decimal ratio) => Math.Round(ratio * 100m, 1, MidpointRounding.AwayFromZero);

    internal static DashboardMoneyView Money(decimal amount, Currency currency)
        => new(Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), currency.Code);

    internal sealed record Sum(IReadOnlyDictionary<Currency, decimal> PerCurrency, decimal? Total, string? Missing)
    {
        public static readonly Sum Nothing = new(new Dictionary<Currency, decimal>(), null, null);

        public static Sum Of(IReadOnlyDictionary<Currency, decimal> sums, Currency reference, RateTable rates, LocalDate today)
        {
            if (sums.Count == 0) return Nothing;
            var total = 0m;
            // Ordered, so the currency a miss names is stable.
            foreach (var (currency, amount) in sums.OrderBy(s => s.Key.Code, StringComparer.Ordinal))
            {
                if (amount == 0m) continue; // zero converts to zero at any rate
                if (!rates.TryConvert(amount, currency, reference, today, projected: false, out var converted, out _))
                    return new Sum(sums, null, currency.Code); // the sum that could not convert, even when the miss is the reference quote
                total += converted;
            }
            return new Sum(sums, total, null);
        }

        public DashboardFigure View(Currency reference) => new(
            PerCurrency.OrderBy(s => s.Key.Code, StringComparer.Ordinal).Select(s => DashboardMath.Money(s.Value, s.Key)).ToList(),
            Total is { } t ? DashboardMath.Money(t, reference) : null,
            Missing);
    }
}
