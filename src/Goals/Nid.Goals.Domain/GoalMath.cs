using Nid.Goals.Contracts;
using NodaTime;

namespace Nid.Goals.Domain;

public sealed record GoalProgress(decimal Target, decimal Reached, decimal ProgressPct, decimal Remaining, decimal? MonthlyNeeded, decimal? MonthlyPace, LocalDate? ProjectedOn, string Status);

/// <summary>An envelope's contribution to a goal, amounts already converted to the goal's currency.</summary>
public sealed record FundingInput(decimal? Value, decimal NetContributions12Months, int ShareBp, bool Closed);

public sealed record ProgressEntry(LocalDate On, decimal Amount);

public static class GoalMath
{
    private const decimal DaysPerMonth = 30.4375m;

    /// <summary>Spec §3: an amount rounded to the cent, as every goal figure.</summary>
    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Spec §3.4. Null when there is no target to measure against.</summary>
    public static GoalProgress? Evaluate(decimal target, decimal reached, decimal? monthlyPace, LocalDate? targetDate, LocalDate today)
    {
        if (target <= 0) return null;
        var reachedR = R2(reached);
        var remaining = Math.Max(0, target - reachedR);
        var pct = Math.Clamp(Math.Round(reached / target * 100m, 1, MidpointRounding.AwayFromZero), 0m, 100m);
        var pace = monthlyPace is { } p ? R2(p) : (decimal?)null;
        if (reachedR >= target)
            return new(target, reachedR, pct, 0m, 0m, pace, null, GoalStatuses.Reached);

        decimal? needed = null;
        if (targetDate is { } due)
        {
            var months = due <= today ? 1 : Math.Max(1, Period.Between(today, due, PeriodUnits.Months).Months);
            needed = R2(remaining / months);
        }

        // Beyond 100 years the projection is noise (and would overflow PlusMonths).
        LocalDate? projected = monthlyPace is > 0 && remaining > 0 && Math.Ceiling(remaining / monthlyPace.Value) is <= 1200m and var m
            ? today.PlusMonths((int)m)
            : null;

        var status = targetDate is not { } d ? GoalStatuses.NoDate
            : d < today ? GoalStatuses.PastDue
            : projected is { } pj && pj <= d ? GoalStatuses.OnTrack
            : GoalStatuses.Behind;
        return new(target, reachedR, pct, remaining, needed, pace, projected, status);
    }

    public static decimal ReachedFromEnvelopes(IEnumerable<FundingInput> funding)
        => funding.Where(f => !f.Closed).Sum(f => (f.Value ?? 0m) * f.ShareBp / 10_000m);

    public static decimal PaceFromEnvelopes(IEnumerable<FundingInput> funding)
        => funding.Where(f => !f.Closed).Sum(f => f.NetContributions12Months * f.ShareBp / 10_000m) / 12m;

    public static decimal ReachedFromEntries(IEnumerable<ProgressEntry> entries)
        => entries.OrderByDescending(e => e.On).FirstOrDefault()?.Amount ?? 0m;

    /// <summary>Spec §3.3 free goal: gain since the entry a year ago (else the first one), per month.</summary>
    public static decimal? PaceFromEntries(IEnumerable<ProgressEntry> entries, LocalDate today)
    {
        var sorted = entries.OrderBy(e => e.On).ToList();
        if (sorted.Count < 2) return null;
        var latest = sorted[^1];
        var cutoff = today.PlusMonths(-12);
        var reference = sorted.LastOrDefault(e => e.On <= cutoff) ?? sorted[0];
        if (reference == latest) return null;
        var months = Math.Max(1m, Period.Between(reference.On, latest.On, PeriodUnits.Days).Days / DaysPerMonth);
        return (latest.Amount - reference.Amount) / months;
    }

    public static decimal EmergencyTarget(int months, decimal monthlyCost) => months * monthlyCost;

    public static int FreeShareBp(IEnumerable<int> otherOpenSharesBp) => Math.Max(0, 10_000 - otherOpenSharesBp.Sum());
}
