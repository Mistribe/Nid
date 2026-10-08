using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Dashboard;

public sealed record DashboardMoneyView(string Amount, string Currency);

/// <summary>A figure's per-currency sums and their total in the household currency: null with nothing to sum, or when a rate is missing (then <c>MissingCurrency</c> names it).</summary>
public sealed record DashboardFigure(IReadOnlyList<DashboardMoneyView> PerCurrency, DashboardMoneyView? Total, string? MissingCurrency);

public sealed record DashboardView(
    string Currency,
    DashboardFigure NetWorth, LocalDate? ValuesAsOf, int Envelopes, int EnvelopesWithoutValue, int StaleEnvelopes,
    DashboardFigure Commitments, DashboardFigure CashThisMonth, int Subscriptions,
    DashboardFigure Income, int IncomeSources, DashboardMoneyView? Margin, decimal? CommittedPct,
    DashboardFigure Change, DashboardFigure ChangeContributions, decimal? ChangePct);

/// <summary>
/// 2D1 spec §4: sums the modules' parts per currency, converts each sum at today's rate, and derives the margin, the share
/// committed and the change percent. Exact decimals throughout; rounded only on the wire.
/// </summary>
public static class DashboardMath
{
    public static IReadOnlySet<Currency> Currencies(
        IEnumerable<NetWorthPart> worth, IEnumerable<CommitmentsPart> commitments, IEnumerable<IncomePart> income)
        => worth.SelectMany(p => p.Values.Keys.Concat(p.StartValues.Keys).Concat(p.NetContributions.Keys).Concat(p.Gains.Keys))
            .Concat(commitments.SelectMany(p => p.Amortized.Keys.Concat(p.Cash.Keys)))
            .Concat(income.SelectMany(p => p.MonthlyEquivalent.Keys))
            .ToHashSet();

    public static DashboardView Compose(
        IReadOnlyList<NetWorthPart> worth, IReadOnlyList<CommitmentsPart> commitments, IReadOnlyList<IncomePart> income,
        Currency reference, RateTable rates, LocalDate today)
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
        decimal? committed = amortized.Missing is null ? amortized.Total ?? 0m : null;
        decimal? margin = incomeTotal is { } i && committed is { } c ? i - c : null;
        decimal? share = incomeTotal is { } i2 && i2 > 0m && committed is { } c2 ? Percent(c2 / i2) : null;
        decimal? changePct = gain.Total is { } g && start.Total is { } s && net.Total is { } n && s + n > 0m ? Percent(g / (s + n)) : null;

        return new DashboardView(
            reference.Code,
            netWorth.View(reference), worth.Select(p => p.OldestAsOf).Min(), worth.Sum(p => p.Envelopes),
            worth.Sum(p => p.WithoutValue), worth.Sum(p => p.Stale),
            amortized.View(reference), cash.View(reference), commitments.Sum(p => p.Subscriptions),
            sources > 0 ? monthly.View(reference) : Sum.Nothing.View(reference), sources,
            margin is { } m ? Money(m, reference) : null, share,
            gain.View(reference), net.View(reference), changePct);
    }

    private static IReadOnlyDictionary<Currency, decimal> Merge(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts)
        => parts.SelectMany(p => p).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

    private static decimal Percent(decimal ratio) => Math.Round(ratio * 100m, 1, MidpointRounding.AwayFromZero);

    private static DashboardMoneyView Money(decimal amount, Currency currency)
        => new(Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), currency.Code);

    private sealed record Sum(IReadOnlyDictionary<Currency, decimal> PerCurrency, decimal? Total, string? Missing)
    {
        public static readonly Sum Nothing = new(new Dictionary<Currency, decimal>(), null, null);

        public static Sum Of(IReadOnlyDictionary<Currency, decimal> sums, Currency reference, RateTable rates, LocalDate today)
        {
            if (sums.Count == 0) return Nothing;
            var total = 0m;
            // Ordered, so the currency a miss names is stable.
            foreach (var (currency, amount) in sums.OrderBy(s => s.Key.Code, StringComparer.Ordinal))
            {
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
