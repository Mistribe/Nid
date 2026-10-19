using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>Holdings spec §3–§4. Pure, exact decimals, in the envelope's currency.</summary>
public static class HoldingMath
{
    /// <summary>ISO 6166: two letters, nine letters or digits, a Luhn check digit over the letters read as 10–35.</summary>
    public static bool IsIsin(string? isin)
    {
        if (isin is not { Length: 12 } || !char.IsAsciiLetterUpper(isin[0]) || !char.IsAsciiLetterUpper(isin[1])
            || !isin[2..11].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)) || !char.IsAsciiDigit(isin[11]))
            return false;
        var digits = string.Concat(isin.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString()));
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    public static decimal Total(HoldingSnapshot snapshot) => snapshot.Lines.Sum(l => l.Value);

    /// <summary>Each line's share of the snapshot's total, rounded to 0.1 %; null when the total is zero.</summary>
    public static decimal? Weight(HoldingSnapshot snapshot, SnapshotLine line)
        => Total(snapshot) is var total && total == 0 ? null : EnvelopeMath.RoundPercent(line.Value / total * 100m);

    /// <summary>
    /// The value as printed against units × price (more than 0.01 apart), and the total against the envelope's valuation of the same
    /// date (more than 1 apart). Warnings only: the household validates (spec D6).
    /// </summary>
    public static IReadOnlyList<HoldingWarning> Warnings(HoldingSnapshot snapshot, decimal? valuationSameDay)
    {
        var warnings = snapshot.Lines
            .Where(l => l is { Units: { } u, UnitPrice: { } p } && Math.Abs(u * p - l.Value) > 0.01m)
            .Select(l => new HoldingWarning(HoldingWarnings.ValueMismatch, l.HoldingId))
            .ToList();
        if (valuationSameDay is { } valuation && Math.Abs(Total(snapshot) - valuation) > 1m)
            warnings.Add(new HoldingWarning(HoldingWarnings.SumMismatch));
        return warnings;
    }

    /// <summary>
    /// The holding's value at <paramref name="d"/>: its line in the latest snapshot on or before it that has one, or zero in a later complete
    /// snapshot without it (a holding absent from a partial snapshot is not zero, spec §2.2). Null before any.
    /// </summary>
    public static (LocalDate AsOf, decimal Value)? ValueAt(Guid holding, IReadOnlyList<HoldingSnapshot> snapshots, LocalDate d)
        => snapshots.Where(s => s.AsOf <= d).OrderByDescending(s => s.AsOf)
            .Select(s => (s.AsOf, Line: s.Lines.FirstOrDefault(l => l.HoldingId == holding), s.Complete))
            .Where(s => s.Line is not null || s.Complete)
            .Select(s => ((LocalDate AsOf, decimal Value)?)(s.AsOf, s.Line?.Value ?? 0m))
            .FirstOrDefault();

    /// <summary>Issue #34 §3 at the holding's latest value on or before <paramref name="d"/>; null without one.</summary>
    public static HoldingFigures? At(Guid holding, IReadOnlyList<HoldingSnapshot> snapshots, IReadOnlyList<HoldingMovement> movements, Currency currency, LocalDate d)
    {
        if (ValueAt(holding, snapshots, d) is not var (v, value))
            return null;
        var mine = movements.Where(m => m.HoldingId == holding && m.OccurredOn <= v).ToList();
        var moneyIn = mine.Where(m => MovementKinds.IsMoneyIn(m.Kind)).Sum(m => m.Amount);
        var moneyOut = mine.Where(m => MovementKinds.IsMoneyOut(m.Kind)).Sum(m => m.Amount);
        var gain = value + moneyOut - moneyIn;
        var fees = ExplicitFees(mine, v);
        var cash = mine.Where(m => MovementKinds.IsMoneyIn(m.Kind) || MovementKinds.IsMoneyOut(m.Kind))
            .Select(m => (m.OccurredOn, MovementKinds.IsMoneyIn(m.Kind) ? -m.Amount : m.Amount))
            .Append((v, value));
        return new HoldingFigures(v, EnvelopeMath.M(value, currency), EnvelopeMath.M(moneyIn, currency), EnvelopeMath.M(moneyOut, currency),
            EnvelopeMath.M(gain, currency), moneyIn > 0 ? EnvelopeMath.RoundPercent(gain / moneyIn * 100m) : null,
            Xirr.AnnualPercent(cash, v), EnvelopeMath.M(fees, currency), EnvelopeMath.M(gain + fees, currency));
    }

    /// <summary>
    /// Issue #34 §3: value at the end − value at the start − (money in − out on ]start, end]), between the holding's values on or before
    /// each date; additive like the envelope's. <c>NetContributions</c> is that money in − out.
    /// </summary>
    public static PeriodPerformance Period(Guid holding, IReadOnlyList<HoldingSnapshot> snapshots, IReadOnlyList<HoldingMovement> movements,
        Currency currency, LocalDate from, LocalDate to)
    {
        var start = ValueAt(holding, snapshots, from);
        var end = ValueAt(holding, snapshots, to);
        Money? Of((LocalDate AsOf, decimal Value)? x) => x is { } y ? EnvelopeMath.M(y.Value, currency) : null;
        if (start is not { } a || end is not { } b)
            return new PeriodPerformance(from, to, start?.AsOf, Of(start), end?.AsOf, Of(end), null, null);
        var net = NetIn(holding, movements, m => m.OccurredOn > a.AsOf && m.OccurredOn <= b.AsOf);
        return new PeriodPerformance(from, to, a.AsOf, Of(a), b.AsOf, Of(b), EnvelopeMath.M(net, currency), EnvelopeMath.M(b.Value - a.Value - net, currency));
    }

    /// <summary>One point per snapshot that values the holding: its value and the money in − out dated on or before it.</summary>
    public static IReadOnlyList<SeriesPointFigures> Series(Guid holding, IReadOnlyList<HoldingSnapshot> snapshots, IReadOnlyList<HoldingMovement> movements, Currency currency)
        => snapshots.Select(s => ValueAt(holding, snapshots, s.AsOf)).OfType<(LocalDate AsOf, decimal Value)>()
            .DistinctBy(x => x.AsOf).OrderBy(x => x.AsOf)
            .Select(x => new SeriesPointFigures(x.AsOf, EnvelopeMath.M(x.Value, currency),
                EnvelopeMath.M(NetIn(holding, movements, m => m.OccurredOn <= x.AsOf), currency)))
            .ToList();

    /// <summary>Σ <c>fee</c> amounts dated on or before <paramref name="d"/>, of every holding in <paramref name="movements"/>.</summary>
    public static decimal ExplicitFees(IEnumerable<HoldingMovement> movements, LocalDate d)
        => movements.Where(m => m.Kind == MovementKinds.Fee && m.OccurredOn <= d).Sum(m => m.Amount);

    /// <summary>
    /// Issue #34 §2.3: on a date with allocations, they sum to the envelope's payments in (opening and contributions) or withdrawals of that
    /// date (more than 0.01 apart: <c>allocation_mismatch</c>); the legs of a switch balance within 1 (<c>switch_unbalanced</c>).
    /// </summary>
    public static IReadOnlyList<HoldingWarning> Warnings(IReadOnlyList<HoldingMovement> movements, IReadOnlyList<Flow> flows)
    {
        decimal FlowsOn(LocalDate d, Func<FlowKind, bool> kind) => flows.Where(f => f.OccurredOn == d && kind(f.Kind)).Sum(f => f.Amount.Amount);
        var allocations = movements.Where(m => m.Kind is MovementKinds.AllocationIn or MovementKinds.AllocationOut)
            .GroupBy(m => (m.OccurredOn, m.Kind))
            .Where(g => Math.Abs(g.Sum(m => m.Amount) - FlowsOn(g.Key.OccurredOn, g.Key.Kind == MovementKinds.AllocationIn
                ? k => k is FlowKind.Opening or FlowKind.Contribution : k => k == FlowKind.Withdrawal)) > 0.01m)
            .Select(g => g.Key.OccurredOn).Distinct()
            .Select(d => new HoldingWarning(HoldingWarnings.AllocationMismatch, null, d));
        var switches = movements.Where(m => m.SwitchRef is not null).GroupBy(m => m.SwitchRef)
            .Where(g => Math.Abs(g.Sum(m => MovementKinds.IsMoneyIn(m.Kind) ? m.Amount : -m.Amount)) > 1m)
            .Select(g => new HoldingWarning(HoldingWarnings.SwitchUnbalanced, null, g.Min(m => m.OccurredOn)));
        return allocations.Concat(switches).OrderBy(w => w.On).ToList();
    }

    private static decimal NetIn(Guid holding, IReadOnlyList<HoldingMovement> movements, Func<HoldingMovement, bool> window)
        => movements.Where(m => m.HoldingId == holding && window(m))
            .Sum(m => MovementKinds.IsMoneyIn(m.Kind) ? m.Amount : MovementKinds.IsMoneyOut(m.Kind) ? -m.Amount : 0m);
}
