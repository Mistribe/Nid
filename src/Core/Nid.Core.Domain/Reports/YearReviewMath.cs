using Nid.Core.Contracts;
using Nid.Core.Domain.Dashboard;
using NodaTime;

namespace Nid.Core.Domain.Reports;

/// <summary>Issue #12: the engagements of a period in the household currency; <c>Missing</c> names a currency without a rate.</summary>
public sealed record EngagementsSum(decimal? Total, IReadOnlyList<(string Source, decimal? Amount, bool Estimated)> Sources, string? Missing);

/// <summary>Issue #12: a part of the net worth's change, and its bar (the running total from the start before and after it), to the cent.</summary>
public sealed record NetWorthStep(string Key, decimal Amount, decimal Low, decimal High);

/// <summary>Issue #12: the net worth on two dates and its change in parts, in the household currency.</summary>
public sealed record NetWorthChange(decimal Start, decimal End, decimal Change, IReadOnlyList<NetWorthStep> Parts);

/// <summary>Issue #12: the year review's figures: the net worth's change in parts, the engagements of a period, the years on offer.</summary>
public static class YearReviewMath
{
    /// <summary>How far back the years on offer reach.</summary>
    public const int MaxYears = IIncomeReview.MaxYears;

    /// <summary>
    /// The net worth's parts on <paramref name="from"/> and <paramref name="to"/>, each date at its own rate, as the curve does, the
    /// largest first. A part is the step of the running total from the start, both ends rounded to the cent, so the parts add up to the
    /// change exactly; a step of zero is left out. Null when a rate is missing (then <paramref name="missing"/> names its currency).
    /// </summary>
    public static NetWorthChange? Split(
        IReadOnlyList<MonthPart> parts, Currency reference, RateTable rates, LocalDate from, LocalDate to, out string? missing)
    {
        missing = null;
        decimal start = 0m, end = 0m;
        var changes = new List<(string Key, decimal Change)>();
        foreach (var group in parts.GroupBy(p => p.Key))
        {
            var b = DashboardMath.Sum.Of(DashboardMath.Merge(group.Select(p => p.Before)), reference, rates, from);
            var n = DashboardMath.Sum.Of(DashboardMath.Merge(group.Select(p => p.Now)), reference, rates, to);
            missing = b.Missing ?? n.Missing;
            if (missing is not null) return null;
            start += b.Total ?? 0m;
            end += n.Total ?? 0m;
            changes.Add((group.Key, (n.Total ?? 0m) - (b.Total ?? 0m)));
        }

        var steps = new List<NetWorthStep>();
        var running = start;
        foreach (var (key, change) in changes.OrderByDescending(c => Math.Abs(c.Change)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var before = Cents(running);
            running += change;
            var after = Cents(running);
            if (after == before) continue;
            steps.Add(new NetWorthStep(key, after - before, Math.Min(before, after), Math.Max(before, after)));
        }
        // The running total ends on the end exactly: the parts are the same sums.
        return new NetWorthChange(start, end, Cents(end) - Cents(start), steps);
    }

    /// <summary>To the cent, as every figure shown.</summary>
    public static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Each month's cash due by source, at the rate of its date (<paramref name="months"/>: one per month, read on that date), and the
    /// year's tax (in euros, as the estimate reads it) for as many twelfths as there are months, at the last month's rate; a refund
    /// commits nothing. A source with no cash due and nothing estimated is left out (a loan not yet started); one with a month still
    /// estimated is estimated, as the tax always is.
    /// </summary>
    public static EngagementsSum Engagements(
        IReadOnlyList<(LocalDate On, IReadOnlyList<CommitmentsPart> Parts)> months, decimal yearTax, Currency reference, RateTable rates)
    {
        string? missing = null;
        decimal? Convert(IReadOnlyDictionary<Currency, decimal> cash, LocalDate on)
        {
            var sum = DashboardMath.Sum.Of(cash, reference, rates, on);
            missing ??= sum.Missing;
            return sum.Missing is null ? sum.Total ?? 0m : null;
        }

        // The current month counts its whole cash due; the subscriptions section stops at today.
        var sources = months.SelectMany(m => m.Parts.Select(p => (m.On, Part: p))).GroupBy(x => x.Part.Source)
            .Where(g => g.Any(x => x.Part.Estimated || x.Part.Cash.Values.Any(v => v != 0m)))
            .Select(g => (Source: g.Key, Amount: g.Aggregate((decimal?)0m, (total, x) => Convert(x.Part.Cash, x.On) is { } a ? total + a : null),
                Estimated: g.Any(x => x.Part.Estimated)))
            .ToList();
        var tax = Math.Max(0m, yearTax) * months.Count / 12;
        if (tax > 0m) sources.Add((CommitmentSources.Tax, Convert(new Dictionary<Currency, decimal> { [Currency.Eur] = tax }, months[^1].On), true));
        // Each source to the cent, so the sources add up to the total shown.
        sources = sources.Select(s => s with { Amount = s.Amount is { } a ? Cents(a) : null })
            .OrderBy(s => DashboardMath.SourceRank(s.Source)).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();
        return new EngagementsSum(sources.Any(s => s.Amount is null) ? null : sources.Sum(s => s.Amount!.Value), sources, missing);
    }

    /// <summary>
    /// From the first year whose 31 December (today for the current year) has a holding, or the year of the first income due or charge
    /// (<paramref name="firstFlow"/>) when earlier, up to today's, at most <see cref="MaxYears"/> back; today's year alone without either.
    /// </summary>
    public static IReadOnlyList<int> Years(
        IReadOnlyList<LocalDate> dates, IReadOnlyList<IReadOnlyList<NetWorthPoint>> points, int currentYear, LocalDate? firstFlow = null)
    {
        var first = dates.Where((_, i) => points.Any(p => p[i].Envelopes > 0)).Select(d => d.Year).DefaultIfEmpty(currentYear).First();
        if (firstFlow is { } flow) first = Math.Max(currentYear - MaxYears, Math.Min(first, flow.Year));
        return Enumerable.Range(first, currentYear - first + 1).ToList();
    }
}
