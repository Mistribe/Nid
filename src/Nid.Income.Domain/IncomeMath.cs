using Nid.Core.Contracts;
using Nid.Income.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Domain;

public sealed record Occurrence(Guid PhaseId, string Label, LocalDate On, Money Amount);
public sealed record ExpectedMonth(int Year, int Month, IReadOnlyList<Occurrence> Occurrences, IReadOnlyList<Flow> OneOffs, Money Total);
public sealed record GapWarning(string Label, LocalDate EndsOn);

/// <param name="Kind"><c>start</c>, <c>change</c>, <c>added</c> or <c>ended</c>.</param>
/// <param name="Percent">The change from <paramref name="Before"/> to <paramref name="After"/>; null unless both repeat alike.</param>
public sealed record PhaseChange(LocalDate On, string Kind, Guid PhaseId, string Label, Money? Before, Money? After, decimal? Percent, bool Upcoming);

/// <summary>income-data-model.md §4 and spec §3: pure, exact decimals.</summary>
public static class IncomeMath
{
    public const int GapWindowDays = 60;

    /// <summary>starts_on + k × every, from the anchor each time (Duration.AddTo), before ends_on and under max_cycles.</summary>
    public static IEnumerable<Occurrence> Occurrences(IncomePhase phase, LocalDate from, LocalDate toExclusive)
    {
        for (var k = 0; phase.MaxCycles is not { } max || k < max; k++)
        {
            var on = phase.Every.AddTo(phase.StartsOn, k);
            if (phase.EndsOn is { } end && on >= end || on >= toExclusive)
                yield break;
            if (on >= from)
                yield return new Occurrence(phase.Id, phase.Label, on, phase.NetAmount);
        }
    }

    /// <summary>Full precision; a phase repeats every month(s) or year(s) only.</summary>
    public static decimal MonthlyEquivalent(IncomePhase phase)
        => phase.Every.Unit switch
        {
            DurationUnit.Year => phase.NetAmount.Amount / (12m * phase.Every.Count),
            DurationUnit.Month => phase.NetAmount.Amount / phase.Every.Count,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), "A phase repeats every month(s) or year(s)."),
        };

    /// <summary>
    /// Exclusive: the earlier of <c>ends_on</c> and the date the next occurrence after the last would fall on
    /// (<c>starts_on + max_cycles × every</c>); null when neither bounds the phase.
    /// </summary>
    public static LocalDate? EffectiveEnd(IncomePhase phase) => EffectiveEnd(phase.StartsOn, phase.EndsOn, phase.Every, phase.MaxCycles);

    /// <summary>As <see cref="EffectiveEnd(IncomePhase)"/>, for a phase not stored yet. Never throws: a cap whose date overflows bounds nothing.</summary>
    public static LocalDate? EffectiveEnd(LocalDate startsOn, LocalDate? endsOn, Duration every, int? maxCycles)
    {
        LocalDate? capped = null;
        if (maxCycles is { } max)
        {
            try { capped = every.AddTo(startsOn, max); }
            catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException) { }
        }
        return (endsOn, capped) switch
        {
            ({ } e, { } c) => e < c ? e : c,
            ({ } e, null) => e,
            (null, var c) => c,
        };
    }

    public static bool IsActive(IncomePhase phase, LocalDate on) => phase.StartsOn <= on && (EffectiveEnd(phase) is not { } end || on < end);

    /// <summary>Full-precision sum of the phases active on the date; a Money would refuse more than 4 decimals, so it is rounded only on the wire.</summary>
    public static decimal MonthlyEquivalent(IEnumerable<IncomePhase> phases, LocalDate on)
        => phases.Where(p => IsActive(p, on)).Sum(MonthlyEquivalent);

    /// <summary>
    /// Projection spec §3.2: the monthly equivalent on <paramref name="on"/> (a future date) as the plan reads it. A phase active on
    /// that date counts; a phase starting after <paramref name="today"/> counts from its start. A phase running today (or starting
    /// later) that ended by <paramref name="on"/> with no phase of its label to continue it is extended — often a contract end date,
    /// not a real loss — and <c>Extended</c> reports it. Each chain of phases is extended once: a later phase of the source (any
    /// label) that started on or after its end and is active on <paramref name="on"/>, or is itself extended there, stands in for
    /// it. A phase with a single occurrence (a one-off bonus) is never extended. A closed source extends nothing.
    /// </summary>
    public static (decimal Amount, bool Extended) MonthlyEquivalentExtended(IReadOnlyList<IncomePhase> phases, LocalDate today, LocalDate on, bool sourceClosed)
    {
        var amount = MonthlyEquivalent(phases, on);
        if (sourceClosed)
            return (amount, false);
        // Latest start first: a successor starts strictly after the end of the phase it replaces, so it is decided before it.
        var extended = new HashSet<Guid>();
        foreach (var phase in phases.OrderByDescending(p => p.StartsOn))
        {
            if (!(IsActive(phase, today) || phase.StartsOn > today) || EffectiveEnd(phase) is not { } end || end > on
                || IsOneShot(phase, end) || IsContinued(phase, phases))
                continue;
            if (phases.Any(q => q.Id != phase.Id && q.StartsOn >= end && q.StartsOn <= on && (IsActive(q, on) || extended.Contains(q.Id))))
                continue;
            extended.Add(phase.Id);
            amount += MonthlyEquivalent(phase);
        }
        return (amount, extended.Count > 0);
    }

    private static bool IsOneShot(IncomePhase phase, LocalDate end) => end <= phase.Every.AddTo(phase.StartsOn, 1);

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Every calendar month from <paramref name="from"/>'s to <paramref name="to"/>'s, inclusive.</summary>
    public static IReadOnlyList<ExpectedMonth> Expected(
        IEnumerable<IncomePhase> phases, IEnumerable<Flow> oneOffs, LocalDate from, LocalDate to, Currency currency)
    {
        var end = to.PlusDays(1);
        var occurrences = phases.SelectMany(p => Occurrences(p, from, end)).ToList();
        var flows = oneOffs.Where(f => f.OccurredOn >= from && f.OccurredOn < end).ToList();
        var months = new List<ExpectedMonth>();
        for (var month = new LocalDate(from.Year, from.Month, 1); month <= to; month = month.PlusMonths(1))
        {
            var o = occurrences.Where(x => x.On.Year == month.Year && x.On.Month == month.Month).OrderBy(x => x.On).ThenBy(x => x.Label, StringComparer.Ordinal).ToList();
            var f = flows.Where(x => x.OccurredOn.Year == month.Year && x.OccurredOn.Month == month.Month).OrderBy(x => x.OccurredOn).ThenBy(x => x.Id).ToList();
            var total = o.Sum(x => x.Amount.Amount) + f.Sum(x => x.Amount.Amount);
            months.Add(new ExpectedMonth(month.Year, month.Month, o, f,
                Money.Create(total, currency).TryGetValue(out var m) ? m : throw new InvalidOperationException()));
        }
        return months;
    }

    /// <summary>Another phase of the same label runs on from where this one effectively ends (a raise, a renewed contract).</summary>
    public static bool IsContinued(IncomePhase phase, IReadOnlyList<IncomePhase> phases)
        => phases.Any(q => q.Id != phase.Id && q.Label == phase.Label && q.StartsOn <= EffectiveEnd(phase)
                           && (EffectiveEnd(q) is not { } qe || qe > EffectiveEnd(phase)));

    /// <summary>income_gap_soon (spec §3): a running phase ending within 60 days that no phase of its label continues.</summary>
    public static IReadOnlyList<GapWarning> Gaps(IEnumerable<IncomePhase> phases, LocalDate today)
    {
        var list = phases.ToList();
        return list
            .Where(p => IsActive(p, today) && EffectiveEnd(p) is { } end && end <= today.PlusDays(GapWindowDays))
            .Where(p => !IsContinued(p, list))
            .Select(p => new GapWarning(p.Label, EffectiveEnd(p)!.Value))
            .OrderBy(g => g.EndsOn).ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The occurrences of the first date after today with any; a phase's next one is at most one cadence after today or after its start.</summary>
    public static IReadOnlyList<Occurrence> Next(IEnumerable<IncomePhase> phases, LocalDate today)
        => phases.SelectMany(p => Occurrences(p, today.PlusDays(1), p.Every.AddTo(LocalDate.Max(p.StartsOn, today), 1).PlusDays(1)))
            .GroupBy(o => o.On).MinBy(g => g.Key)?.OrderBy(o => o.Label, StringComparer.Ordinal).ToList() ?? [];

    /// <summary>
    /// Income dashboard spec §4, the timeline of a source, newest first: the phases starting on its first day <c>start</c> it,
    /// the first of a later label is <c>added</c>, a phase after one of its label is a <c>change</c>, a phase no other continues
    /// has <c>ended</c> — so a label resumed after a gap reads <c>ended</c>, then <c>change</c>.
    /// </summary>
    public static IReadOnlyList<PhaseChange> Changes(IEnumerable<IncomePhase> phases, LocalDate today)
    {
        var list = phases.OrderBy(p => p.StartsOn).ThenBy(p => p.Id).ToList();
        var changes = new List<PhaseChange>();
        for (var i = 0; i < list.Count; i++)
        {
            var phase = list[i];
            var previous = list.Take(i).LastOrDefault(p => p.Label == phase.Label);
            changes.Add(previous is null
                ? new PhaseChange(phase.StartsOn, phase.StartsOn == list[0].StartsOn ? "start" : "added", phase.Id, phase.Label,
                    null, phase.NetAmount, null, phase.StartsOn > today)
                : new PhaseChange(phase.StartsOn, "change", phase.Id, phase.Label, previous.NetAmount, phase.NetAmount,
                    previous.Every == phase.Every ? OverviewMath.ChangePct(phase.NetAmount.Amount, previous.NetAmount.Amount) : null,
                    phase.StartsOn > today));
            if (EffectiveEnd(phase) is { } end && !IsContinued(phase, list))
                changes.Add(new PhaseChange(end, "ended", phase.Id, phase.Label, phase.NetAmount, null, null, end > today));
        }
        return changes.OrderByDescending(c => c.On).ThenBy(c => c.Label, StringComparer.Ordinal).ThenBy(c => c.Kind, StringComparer.Ordinal).ToList();
    }
}
