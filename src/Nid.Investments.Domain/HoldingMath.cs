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
    /// Issue #46: how far the printed units may be from the real ones, truncated or rounded to the decimals they were given with
    /// (<c>1.185</c>: 0.001). Whole units are an exact count. The decimals are the ones typed: stored unscaled, never padded.
    /// </summary>
    public static decimal UnitsStep(decimal units) => units.Scale == 0 ? 0m : new decimal(1, 0, 0, false, units.Scale);

    /// <summary>
    /// The value as printed against units × price (more than 0.01 apart, plus what the printed units leave out: <see cref="UnitsStep"/> ×
    /// price), and the total against the envelope's valuation of the same date (more than 1 apart). Warnings only: the household
    /// validates (spec D6).
    /// </summary>
    public static IReadOnlyList<HoldingWarning> Warnings(HoldingSnapshot snapshot, decimal? valuationSameDay)
    {
        var warnings = snapshot.Lines
            .Where(l => l is { Units: { } u, UnitPrice: { } p } && Math.Abs(u * p - l.Value) > 0.01m + UnitsStep(u) * p)
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

    /// <summary>Issue #34 §2.5: gross − fees − net more than 0.01 pt from zero.</summary>
    public static bool IsInconsistent(CostReport report)
        => Math.Abs(report.GrossReturnPct - report.TotalFeesPct - report.NetReturnPct) > 0.01m;

    /// <summary>
    /// Issue #34 §3, an estimate: the year's <c>total_fees_pct</c> × the mean of the holding's value at the start (on or before 31 December of
    /// the year before) and at the end (on or before 31 December) of <paramref name="year"/>, to the cent. Null without both values.
    /// </summary>
    public static Money? ImplicitCost(CostReport report, IReadOnlyList<HoldingSnapshot> snapshots, Currency currency)
    {
        if (ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year - 1, 12, 31)) is not var (_, start)
            || ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year, 12, 31)) is not var (_, end))
            return null;
        return EnvelopeMath.M(Math.Round(report.TotalFeesPct / 100m * (start + end) / 2m, 2, MidpointRounding.AwayFromZero), currency);
    }

    /// <summary>
    /// Issue #34 §3: between two consecutive snapshots giving a holding's units, Δunits − Σ movement units on ]earlier, later] warns
    /// <c>unexplained_units</c> on the later date. Units gone missing with a later unit price propose a <c>fee</c> of that many units at that
    /// price, to the cent — never written. Issue #46: a movement without units counts amount ÷ its unit price, or ÷ the mean of the two
    /// snapshots' prices; the tolerance is 0.0001, plus <see cref="UnitsStep"/> of both snapshots' units, plus, for each such estimate,
    /// the units its amount buys between the two prices.
    /// </summary>
    public static IReadOnlyList<HoldingWarning> UnexplainedUnits(IReadOnlyList<HoldingSnapshot> snapshots, IReadOnlyList<HoldingMovement> movements, Currency currency)
        => snapshots.SelectMany(s => s.Lines.Where(l => l.Units is not null).Select(l => (s.AsOf, Line: l)))
            .GroupBy(x => x.Line.HoldingId)
            .SelectMany(g =>
            {
                var points = g.OrderBy(x => x.AsOf).ToList();
                return points.Zip(points.Skip(1), (a, b) => (a, b))
                    .Select(p =>
                    {
                        var moved = movements.Where(m => m.HoldingId == g.Key && m.OccurredOn > p.a.AsOf && m.OccurredOn <= p.b.AsOf)
                            .Select(m => MovedUnits(m, p.a.Line.UnitPrice, p.b.Line.UnitPrice)).ToList();
                        var tolerance = 0.0001m + UnitsStep(p.a.Line.Units!.Value) + UnitsStep(p.b.Line.Units!.Value) + moved.Sum(x => x.Spread);
                        return (p.b, Missing: p.b.Line.Units!.Value - p.a.Line.Units!.Value - moved.Sum(x => x.Units), Tolerance: tolerance);
                    })
                    .Where(x => Math.Abs(x.Missing) > x.Tolerance)
                    .Select(x => new HoldingWarning(HoldingWarnings.UnexplainedUnits, g.Key, x.b.AsOf, x.Missing,
                        x.Missing < 0 && x.b.Line.UnitPrice is { } price
                            ? EnvelopeMath.M(Math.Round(-x.Missing * price, 2, MidpointRounding.AwayFromZero), currency) : null));
            })
            .OrderBy(w => w.On).ThenBy(w => w.HoldingId)
            .ToList();

    /// <summary>
    /// Issue #34 §2.6: the share of units-of-account lines (neither euro fund nor cash) in the snapshot's total, rounded to 0.1 %; null when the
    /// total is zero.
    /// </summary>
    public static decimal? UcSharePct(HoldingSnapshot snapshot, IReadOnlyDictionary<Guid, string> kinds)
        => Total(snapshot) is var total && total == 0 ? null
            : EnvelopeMath.RoundPercent(snapshot.Lines.Where(l => kinds.TryGetValue(l.HoldingId, out var k) && HoldingWire.HasUnits(k)).Sum(l => l.Value) / total * 100m);

    /// <summary>
    /// Spec §5.1 (D14, D15): the line's units (value ÷ unit price without them) plus the units of the holding's movements on
    /// ]<paramref name="after"/>, <paramref name="on"/>]; a movement without units or price buys at <paramref name="priceOn"/> its date,
    /// else at the line's unit price. Null when the line's units, or a movement's, cannot be known.
    /// </summary>
    public static decimal? UnitsAt(SnapshotLine line, IReadOnlyList<HoldingMovement> movements, LocalDate after, LocalDate on, Func<LocalDate, decimal?> priceOn)
    {
        var units = line.Units ?? (line.UnitPrice is { } p and > 0 ? line.Value / p : null);
        foreach (var m in movements.Where(m => m.HoldingId == line.HoldingId && m.OccurredOn > after && m.OccurredOn <= on))
            units += OwnUnits(m) ?? ((priceOn(m.OccurredOn) ?? line.UnitPrice) is { } price and > 0 ? Sign(m) * m.Amount / price : null);
        return units;
    }

    /// <summary>
    /// A movement's units: as given, else its amount at its own price (exact), else at the mean of the two snapshots' prices, its
    /// <c>Spread</c> the units the amount buys between them. Without any price, no units (as before issue #46).
    /// </summary>
    private static (decimal Units, decimal Spread) MovedUnits(HoldingMovement m, decimal? before, decimal? after)
    {
        if (OwnUnits(m) is { } units)
            return (units, 0m);
        if (before is not ({ } a and > 0) || after is not ({ } b and > 0))
            return (0m, 0m);
        return (Sign(m) * m.Amount / ((a + b) / 2m), Math.Abs(m.Amount / a - m.Amount / b));
    }

    /// <summary>A movement's units as given, else its amount at its own price; null without either.</summary>
    private static decimal? OwnUnits(HoldingMovement m) => m.Units ?? (m.UnitPrice is { } price and > 0 ? Sign(m) * m.Amount / price : null);

    private static decimal Sign(HoldingMovement m) => MovementKinds.IsIn(m.Kind) ? 1m : -1m;

    internal static decimal NetIn(Guid holding, IReadOnlyList<HoldingMovement> movements, Func<HoldingMovement, bool> window)
        => movements.Where(m => m.HoldingId == holding && window(m))
            .Sum(m => MovementKinds.IsMoneyIn(m.Kind) ? m.Amount : MovementKinds.IsMoneyOut(m.Kind) ? -m.Amount : 0m);
}
