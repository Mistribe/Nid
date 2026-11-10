using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Income.Domain;

public enum OverviewPeriod { Year, Last12Months, ThreeYears }

/// <summary>An amount due on a date, already converted and weighted by the caller; null when it cannot be converted.</summary>
public sealed record Due(LocalDate On, decimal? Amount);

/// <param name="Kind">An IncomeWire kind or "rent".</param>
/// <param name="MonthlyAt">Monthly equivalent at today, today - 1 year and today - 3 years.</param>
public sealed record OverviewInput(
    ItemId Item, string Name, string Kind,
    IReadOnlyList<Due> Recurring, IReadOnlyList<Due> OneOffs,
    IReadOnlyDictionary<LocalDate, decimal?> MonthlyAt);

public sealed record OverviewWindow(LocalDate From, LocalDate To, LocalDate PreviousFrom, LocalDate PreviousTo);

public sealed record OverviewMonth(int Year, int Month, bool Upcoming, decimal? Recurring, decimal? OneOff, decimal? Total,
    decimal? PreviousYear, IReadOnlyList<(ItemId Item, string Name, decimal Amount)> BySource);

public sealed record OverviewShare(ItemId? Item, string Name, string Kind, decimal Total, decimal PerMonth, decimal Percent);

/// <summary>A share of today's monthly income; <c>other</c> gathers the smallest inputs and carries no item.</summary>
public sealed record NowShare(ItemId? Item, string Name, string Kind, decimal PerMonth, decimal Percent);

/// <summary>An input's income over a period; <c>one_off</c> gathers every one-off and carries no item; null when a due cannot be converted.</summary>
public sealed record SourceTotal(ItemId? Item, string Name, string Kind, decimal? Amount);

public sealed record OverviewFigures(decimal? MonthlyNow, decimal? MonthlyChange1y, decimal? Total, decimal? Elapsed,
    decimal? PreviousTotal, decimal? ChangeVsPrevious, decimal? ChangeVsPreviousPct, decimal? Change3y, decimal? Change3yYearly);

/// <summary>
/// Household income overview, pure: amounts come converted and weighted, null meaning not convertible.
/// Due lists may span wider than the window; every function filters by date itself.
/// </summary>
public static class OverviewMath
{
    public const int MaxShares = 6;

    public static OverviewWindow Window(OverviewPeriod period, LocalDate today)
    {
        var (from, to, shift) = period switch
        {
            OverviewPeriod.Year => (new LocalDate(today.Year, 1, 1), new LocalDate(today.Year, 12, 31), 12),
            OverviewPeriod.Last12Months => (Month(today).PlusMonths(-11), EndOfMonth(today), 12),
            _ => (Month(today).PlusMonths(-35), EndOfMonth(today), 36),
        };
        return new OverviewWindow(from, to, from.PlusMonths(-shift), EndOfMonth(to.PlusMonths(-shift)));
    }

    public static IReadOnlyList<OverviewMonth> Months(IReadOnlyList<OverviewInput> inputs, OverviewWindow w, LocalDate today)
    {
        var months = new List<OverviewMonth>();
        for (var m = Month(w.From); m <= w.To; m = m.PlusMonths(1))
        {
            var end = EndOfMonth(m);
            var previous = m.PlusYears(-1);
            var bySource = inputs
                .Select(i => (i.Item, i.Name, Amount: Sum(i.Recurring.Concat(i.OneOffs), m, end)))
                .Where(s => s.Amount is not null and not 0m)
                .Select(s => (s.Item, s.Name, Amount: s.Amount!.Value))
                .ToList();
            var recurring = Sum(inputs.SelectMany(i => i.Recurring), m, end);
            var oneOff = Sum(inputs.SelectMany(i => i.OneOffs), m, end);
            months.Add(new OverviewMonth(m.Year, m.Month, m > today, recurring, oneOff, recurring + oneOff,
                SumAll(inputs, previous, EndOfMonth(previous)), bySource));
        }
        return months;
    }

    public static IReadOnlyList<OverviewShare> Split(IReadOnlyList<OverviewInput> inputs, OverviewWindow w)
    {
        // A due that cannot be converted makes every share unknown.
        if (SumAll(inputs, w.From, w.To) is null)
            return [];
        var count = (w.To.Year - w.From.Year) * 12 + w.To.Month - w.From.Month + 1;
        var raw = inputs
            .Select(i => (Item: (ItemId?)i.Item, i.Name, i.Kind, Total: Sum(i.Recurring, w.From, w.To) ?? 0m))
            .ToList();
        var oneOff = Sum(inputs.SelectMany(i => i.OneOffs), w.From, w.To) ?? 0m;
        raw.Add((null, "one_off", "one_off", oneOff));
        raw.RemoveAll(r => r.Total == 0m);

        var sorted = raw.OrderByDescending(s => s.Total).ToList();
        var kept = sorted.Take(MaxShares).ToList();
        if (sorted.Count > MaxShares)
            kept.Add((null, "other", "other", sorted.Skip(MaxShares).Sum(s => s.Total)));

        var grand = raw.Sum(s => s.Total);
        return kept
            .Select(s => new OverviewShare(s.Item, s.Name, s.Kind, s.Total, s.Total / count,
                grand == 0m ? 0m : Percent(s.Total / grand)))
            .ToList();
    }

    /// <summary>
    /// Issue #12: each input's recurring income in [<paramref name="from"/>, <paramref name="to"/>], the one-offs merged into
    /// <c>one_off</c>, largest first, an unconvertible one last, ties by name then item; those without income in the period left out.
    /// </summary>
    public static IReadOnlyList<SourceTotal> BySource(IReadOnlyList<OverviewInput> inputs, LocalDate from, LocalDate to)
        => inputs.Select(i => new SourceTotal(i.Item, i.Name, i.Kind, Sum(i.Recurring, from, to)))
            .Append(new SourceTotal(null, "one_off", "one_off", Sum(inputs.SelectMany(i => i.OneOffs), from, to)))
            .Where(s => s.Amount is not 0m)
            .OrderBy(s => s.Amount is null).ThenByDescending(s => s.Amount)
            .ThenBy(s => s.Name, French).ThenBy(s => s.Item?.Value)
            .ToList();

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>
    /// Today's monthly equivalent of each input above 0, largest first, those past <see cref="MaxShares"/> merged into
    /// <c>other</c>; empty when one cannot be converted, as <see cref="Split"/>.
    /// </summary>
    public static IReadOnlyList<NowShare> NowSplit(IReadOnlyList<OverviewInput> inputs, LocalDate today)
    {
        if (inputs.Any(i => i.MonthlyAt.GetValueOrDefault(today) is null))
            return [];
        var sorted = inputs
            .Select(i => (Item: (ItemId?)i.Item, i.Name, i.Kind, PerMonth: i.MonthlyAt[today]!.Value))
            .Where(s => s.PerMonth > 0m)
            .OrderByDescending(s => s.PerMonth).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var kept = sorted.Take(MaxShares).ToList();
        if (sorted.Count > MaxShares)
            kept.Add((null, "other", "other", sorted.Skip(MaxShares).Sum(s => s.PerMonth)));
        var grand = sorted.Sum(s => s.PerMonth);
        return kept.Select(s => new NowShare(s.Item, s.Name, s.Kind, s.PerMonth, Percent(s.PerMonth / grand))).ToList();
    }

    public static OverviewFigures Figures(IReadOnlyList<OverviewInput> inputs, OverviewWindow w, LocalDate today)
    {
        decimal? MonthlyAt(LocalDate on) => inputs.Aggregate((decimal?)0m, (sum, i) => sum + i.MonthlyAt.GetValueOrDefault(on));
        var now = MonthlyAt(today);
        var then = MonthlyAt(today.PlusYears(-3));
        var total = SumAll(inputs, w.From, w.To);
        var previousTotal = SumAll(inputs, w.PreviousFrom, w.PreviousTo);
        return new OverviewFigures(
            now, ChangePct(now, MonthlyAt(today.PlusYears(-1))),
            total, SumAll(inputs, w.From, today < w.To ? today : w.To),
            previousTotal, previousTotal is 0m ? null : total - previousTotal, ChangePct(total, previousTotal), // no history reads « — » (spec §3)
            ChangePct(now, then), YearlyRate(now, then));
    }

    private static LocalDate Month(LocalDate d) => new(d.Year, d.Month, 1);
    private static LocalDate EndOfMonth(LocalDate d) => Month(d).PlusMonths(1).PlusDays(-1);

    private static decimal Percent(decimal ratio) => Math.Round(ratio * 100m, 1, MidpointRounding.AwayFromZero);

    /// <summary>The change from <paramref name="then"/> to <paramref name="now"/> in percent, 1 decimal; null when either is unknown or <paramref name="then"/> is 0.</summary>
    public static decimal? ChangePct(decimal? now, decimal? then)
        => now is { } n && then is { } t && t != 0m ? Percent((n - t) / t) : null;

    /// <summary>The observed inflation over the last 12 months in percent, 1 decimal; null on a series that cannot tell.</summary>
    public static decimal? InflationPct(InflationSeries series) => series.Last12Months is { } l ? Percent(l.Rate) : null;

    private static decimal? YearlyRate(decimal? now, decimal? then)
        => now is { } n && then is { } t && t > 0m && n >= 0m
            ? Math.Round((decimal)(Math.Pow((double)(n / t), 1.0 / 3.0) - 1.0) * 100m, 1, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>Sum of the dues within the dates; null when one of them is not convertible.</summary>
    private static decimal? Sum(IEnumerable<Due> dues, LocalDate from, LocalDate to)
    {
        decimal sum = 0m;
        foreach (var d in dues.Where(d => d.On >= from && d.On <= to))
        {
            if (d.Amount is not { } a) return null;
            sum += a;
        }
        return sum;
    }

    private static decimal? SumAll(IReadOnlyList<OverviewInput> inputs, LocalDate from, LocalDate to)
        => Sum(inputs.SelectMany(i => i.Recurring.Concat(i.OneOffs)), from, to);
}
