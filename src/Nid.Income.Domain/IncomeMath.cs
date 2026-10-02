using Nid.Core.Contracts;
using Nid.Income.Contracts;
using NodaTime;

namespace Nid.Income.Domain;

public sealed record Occurrence(Guid PhaseId, string Label, LocalDate On, Money Amount);
public sealed record ExpectedMonth(int Year, int Month, IReadOnlyList<Occurrence> Occurrences, IReadOnlyList<Flow> OneOffs, Money Total);
public sealed record GapWarning(string Label, LocalDate EndsOn);

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
    public static LocalDate? EffectiveEnd(IncomePhase phase)
    {
        LocalDate? capped = phase.MaxCycles is { } max ? phase.Every.AddTo(phase.StartsOn, max) : null;
        return (phase.EndsOn, capped) switch
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

    /// <summary>income_gap_soon (spec §3): a running phase ending within 60 days that no phase of its label continues.</summary>
    public static IReadOnlyList<GapWarning> Gaps(IEnumerable<IncomePhase> phases, LocalDate today)
    {
        var list = phases.ToList();
        return list
            .Where(p => IsActive(p, today) && EffectiveEnd(p) is { } end && end <= today.PlusDays(GapWindowDays))
            .Where(p => !list.Any(q => q.Id != p.Id && q.Label == p.Label && q.StartsOn <= EffectiveEnd(p) && (EffectiveEnd(q) is not { } qe || qe > EffectiveEnd(p))))
            .Select(p => new GapWarning(p.Label, EffectiveEnd(p)!.Value))
            .OrderBy(g => g.EndsOn).ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();
    }
}
