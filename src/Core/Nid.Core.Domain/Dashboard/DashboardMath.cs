using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Dashboard;

/// <summary>
/// Issue #104: an asset class or a liquidity of the allocation, in the household currency, its share of the assets (0.1 %). With a
/// target for the class: <c>GapPct</c> its share minus its target, and <c>Gap</c> the amount above (+) or below (−) the target.
/// </summary>
public sealed record AllocationLine(string Key, decimal Amount, decimal Pct, decimal? TargetPct, decimal? GapPct, decimal? Gap);

/// <summary>
/// Issue #104: the net worth by asset class and by liquidity. <c>Assets</c> + <c>Debts</c> is the net worth; <c>Approximate</c> is the
/// part of the assets whose class is guessed from a kind; <c>Target</c> the household's, in %.
/// </summary>
public sealed record AllocationFigures(
    decimal Assets, decimal Debts, decimal Approximate, IReadOnlyList<AllocationLine> Classes, IReadOnlyList<AllocationLine> Liquidity,
    IReadOnlyDictionary<string, decimal> Target);

/// <summary>
/// 2D1 spec §4: the modules' parts summed per currency, each sum converted at its day's rate, the budget of the month and the
/// allocation. Exact decimals throughout; rounded only on the wire (<see cref="Money"/>).
/// </summary>
public static class DashboardMath
{
    /// <summary>« À venir » reads the next 60 days.</summary>
    public const int UpcomingDays = 60;

    /// <summary>Every currency the parts hold, for one rate query.</summary>
    public static IReadOnlySet<Currency> Currencies(
        IEnumerable<NetWorthPart> worth, IEnumerable<CommitmentsPart> commitments, IEnumerable<IncomePart> income, IEnumerable<SpendingPart>? spending = null)
        => worth.SelectMany(p => p.Values.Keys.Concat(p.StartValues.Keys).Concat(p.NetContributions.Keys).Concat(p.Gains.Keys))
            .Concat(commitments.SelectMany(p => p.Amortized.Keys.Concat(p.Cash.Keys)))
            .Concat(income.SelectMany(p => p.MonthlyEquivalent.Keys))
            .Concat((spending ?? []).SelectMany(p => p.Total.Keys))
            .ToHashSet();

    /// <summary>
    /// The margin and « Reste réel »'s parts, each sum converted at <paramref name="today"/>'s rate. Issue #425: the spending page reads
    /// them through <see cref="IMonthBudget"/>, so both pages show one figure.
    /// </summary>
    public static MonthBudget Budget(
        IReadOnlyList<CommitmentsPart> commitments, IReadOnlyList<IncomePart> income, IReadOnlyList<SpendingPart> spending,
        Currency reference, RateTable rates, LocalDate today)
    {
        Sum Of(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts) => Sum.Of(Merge(parts), reference, rates, today);

        var amortized = Of(commitments.Select(p => p.Amortized));
        // No income declared reads « inconnu », never 0 (spec §3); no subscription commits nothing.
        decimal? incomeTotal = income.Sum(p => p.Sources) > 0 ? Of(income.Select(p => p.MonthlyEquivalent)).Total : null;
        decimal? committed = amortized.Missing is null ? amortized.Total ?? 0m : null;
        return new MonthBudget(incomeTotal, committed, Of(spending.Select(p => p.Total)).Total);
    }

    /// <summary>
    /// Issue #104: each holding's slices at its own converted rate; a holding without slices is classed by its module (a property
    /// is real estate, locked; a loan is debt). Null without assets above 0.
    /// </summary>
    public static AllocationFigures? Allocation(
        IEnumerable<(Holding Holding, (decimal? Amount, string? Missing) Converted)> holdings, IReadOnlyDictionary<string, decimal> target)
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
            foreach (var slice in SlicesOf(holding, value.Amount))
            {
                var part = slice.Amount * amount / value.Amount; // the holding's own rate, as its converted value
                classes[slice.Class] = classes.GetValueOrDefault(slice.Class) + part;
                liquidity[slice.Liquidity] = liquidity.GetValueOrDefault(slice.Liquidity) + part;
                if (slice.Approximate) approximate += part;
            }
        }
        var assets = classes.Values.Sum();
        if (assets <= 0m) return null;

        AllocationLine Line(string key, decimal amount, decimal? goal)
        {
            var share = amount / assets * 100m;
            return new AllocationLine(key, amount, Math.Round(share, 1, MidpointRounding.AwayFromZero), goal,
                goal is { } g ? Math.Round(share - g, 1, MidpointRounding.AwayFromZero) : null,
                goal is { } t ? amount - assets * t / 100m : null);
        }
        return new AllocationFigures(assets, debts, approximate,
            AllocationClasses.All.Where(c => classes.GetValueOrDefault(c) != 0m || target.ContainsKey(c))
                .Select(c => Line(c, classes.GetValueOrDefault(c), target.TryGetValue(c, out var t) ? t : null)).ToList(),
            AllocationLiquidity.All.Where(l => liquidity.GetValueOrDefault(l) != 0m).Select(l => Line(l, liquidity[l], null)).ToList(),
            target);
    }

    /// <summary>A holding's slices, or without them its module's: a property is real estate, locked; anything else is guessed as other.</summary>
    public static IReadOnlyList<AllocationSlice> SlicesOf(Holding holding, decimal amount)
        => holding.Slices ?? (holding.Module == "properties"
            ? [new AllocationSlice(AllocationClasses.RealEstate, AllocationLiquidity.Locked, amount, false)]
            : [new AllocationSlice(AllocationClasses.Other, AllocationLiquidity.Days, amount, true)]);

    /// <summary>One holding at today's rate: zero converts to zero at any rate, as <see cref="Sum.Of"/>.</summary>
    public static (decimal? Amount, string? Missing) Convert(Money? value, Currency reference, RateTable rates, LocalDate today)
    {
        if (value is not { } v) return (null, null);
        if (v.Amount == 0m) return (0m, null);
        return rates.TryConvert(v.Amount, v.Currency, reference, today, projected: false, out var converted, out _)
            ? (converted, null)
            : (null, v.Currency.Code);
    }

    /// <summary>3E1 spec §3: « Abonnements · Crédits · Charges des biens », then « Impôts ».</summary>
    public static int SourceRank(string source) => source switch
    {
        CommitmentSources.Subscriptions => 0,
        CommitmentSources.Loans => 1,
        CommitmentSources.PropertyCosts => 2,
        CommitmentSources.Tax => 3,
        _ => 4,
    };

    public static IReadOnlyDictionary<Currency, decimal> Merge(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts)
        => parts.SelectMany(p => p).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

    public static decimal Percent(decimal ratio) => Math.Round(ratio * 100m, 1, MidpointRounding.AwayFromZero);

    public static DashboardMoneyView Money(decimal amount, Currency currency)
        => new(Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), currency.Code);

    public sealed record Sum(IReadOnlyDictionary<Currency, decimal> PerCurrency, decimal? Total, string? Missing)
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

    }
}
