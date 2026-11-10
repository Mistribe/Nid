using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>2B1a spec §4; <c>investments-data-model.md</c> §5. Pure, exact decimals, in the envelope's currency.</summary>
public static class EnvelopeMath
{
    public static EnvelopeFigures At(IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, Currency currency,
        ValuationCadence cadence, int? staleAfterDays, LocalDate d)
    {
        var latest = LastOnOrBefore(values, d);
        if (latest is null)
            return new EnvelopeFigures(null, null, null, null, null, null, null, null, null,
                Sum(flows, currency, f => f.Kind == FlowKind.Contribution && f.OccurredOn <= d),
                Sum(flows, currency, f => f.Kind == FlowKind.Withdrawal && f.OccurredOn <= d),
                false, null);

        var v = latest.AsOf;
        var net = NetContributions(flows, f => f.OccurredOn <= v);
        var paidIn = Total(flows, f => f.OccurredOn <= v && f.Kind is FlowKind.Opening or FlowKind.Contribution);
        var value = latest.Value.Amount;
        var gain = value - net;
        var fees = Total(flows, f => f.Kind == FlowKind.Expense && f.OccurredOn <= v);
        var missingOpening = value != 0
            && !flows.Any(f => f.OccurredOn <= v && f.Kind is FlowKind.Opening or FlowKind.Contribution);
        var threshold = StaleThreshold(cadence, staleAfterDays);
        var age = NodaTime.Period.DaysBetween(v, d);

        return new EnvelopeFigures(
            latest.Value, v,
            M(net, currency), M(gain, currency), paidIn > 0 ? RoundPercent(gain / paidIn * 100m) : null,
            AnnualReturn(flows, latest),
            M(fees, currency), M(gain - fees, currency),
            Sum(flows, currency, f => f.Kind == FlowKind.Income && f.OccurredOn <= v),
            Sum(flows, currency, f => f.Kind == FlowKind.Contribution && f.OccurredOn > v && f.OccurredOn <= d),
            Sum(flows, currency, f => f.Kind == FlowKind.Withdrawal && f.OccurredOn > v && f.OccurredOn <= d),
            missingOpening,
            threshold is { } limit && age > limit ? age : null);
    }

    /// <summary>
    /// An envelope with no value on or before <paramref name="from"/> but one on or before <paramref name="to"/> has its <c>StartAsOf</c>
    /// null and counts the flows after <paramref name="from"/>. Opened during the period (no flow on or before <paramref name="from"/>), it
    /// starts at 0; entered with older flows, at its net contributions up to <paramref name="from"/>, no gain invented (<c>NoStartValue</c>).
    /// </summary>
    public static PeriodPerformance Period(IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, Currency currency,
        LocalDate from, LocalDate to)
    {
        var start = LastOnOrBefore(values, from);
        var end = LastOnOrBefore(values, to);
        if (end is null)
            return new PeriodPerformance(from, to, start?.AsOf, start?.Value, null, null, null, null);

        var startValue = start?.Value ?? NetContributionsBy(flows, currency, from);
        var after = start?.AsOf ?? from;
        var net = NetContributions(flows, f => f.OccurredOn > after && f.OccurredOn <= end.AsOf);
        return new PeriodPerformance(from, to, start?.AsOf, startValue, end.AsOf, end.Value,
            M(net, currency), M(end.Value.Amount - startValue.Amount - net, currency),
            NoStartValue: start is null && flows.Any(f => f.OccurredOn <= from));
    }

    /// <summary>The openings and contributions minus the withdrawals dated on or before <paramref name="d"/>.</summary>
    public static Money NetContributionsBy(IReadOnlyList<Flow> flows, Currency currency, LocalDate d)
        => M(NetContributions(flows, f => f.OccurredOn <= d), currency);

    /// <summary>
    /// One point per valuation, by date: the value and the net contributions dated on or before it. Before the first valuation, one
    /// point per date of money in or out, its value the net contributions up to it (<c>PaidIn</c>): a stand-in, no gain invented.
    /// </summary>
    public static IReadOnlyList<SeriesPointFigures> Series(IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, Currency currency)
    {
        var first = values.Count == 0 ? LocalDate.MaxIsoValue : values.Min(v => v.AsOf);
        var paidIn = flows.Where(f => f.OccurredOn < first && f.Kind is FlowKind.Opening or FlowKind.Contribution or FlowKind.Withdrawal)
            .Select(f => f.OccurredOn).Distinct().Order()
            .Select(d =>
            {
                var net = M(NetContributions(flows, f => f.OccurredOn <= d), currency);
                return new SeriesPointFigures(d, net, net, PaidIn: true);
            });
        return paidIn.Concat(values.OrderBy(v => v.AsOf)
            .Select(v => new SeriesPointFigures(v.AsOf, v.Value, M(NetContributions(flows, f => f.OccurredOn <= v.AsOf), currency))))
            .ToList();
    }

    /// <summary>
    /// Projection spec §2.1: the contributions minus the withdrawals dated in [<paramref name="from"/>, <paramref name="to"/>]. Openings are
    /// left out: an envelope created in Nid would otherwise count its whole value as a period of savings.
    /// </summary>
    public static decimal SavedBetween(IReadOnlyList<Flow> flows, LocalDate from, LocalDate to)
        => Total(flows, f => f.OccurredOn >= from && f.OccurredOn <= to && f.Kind == FlowKind.Contribution)
           - Total(flows, f => f.OccurredOn >= from && f.OccurredOn <= to && f.Kind == FlowKind.Withdrawal);

    /// <summary>
    /// <c>investments-data-model.md</c> §6: the money-weighted annual return (XIRR, Actual/365) up to the value at <c>v</c>, in percent;
    /// null when not meaningful (under 12 months, or cash flows all of one sign).
    /// </summary>
    public static decimal? AnnualReturn(IReadOnlyList<Flow> flows, Valuation latest) => Xirr.AnnualPercent(CashFlows(flows, latest), latest.AsOf);

    /// <summary>The XIRR's cash flows: money paid in negative, taken out and the latest value positive.</summary>
    public static IEnumerable<(LocalDate On, decimal Amount)> CashFlows(IReadOnlyList<Flow> flows, Valuation latest)
        => flows.Where(f => f.OccurredOn <= latest.AsOf && f.Kind is FlowKind.Opening or FlowKind.Contribution or FlowKind.Withdrawal)
            .Select(f => (f.OccurredOn, f.Kind == FlowKind.Withdrawal ? f.Amount.Amount : -f.Amount.Amount))
            .Append((latest.AsOf, latest.Value.Amount));

    public static decimal RoundPercent(decimal percent) => Math.Round(percent, 1, MidpointRounding.AwayFromZero);

    public static int? StaleThreshold(ValuationCadence cadence, int? staleAfterDays) => staleAfterDays ?? cadence switch
    {
        ValuationCadence.Monthly => 45,
        ValuationCadence.Quarterly => 120,
        ValuationCadence.Yearly => 400,
        _ => null,
    };

    public static Valuation? LastOnOrBefore(IReadOnlyList<Valuation> values, LocalDate d)
        => values.Where(x => x.AsOf <= d).OrderByDescending(x => x.AsOf).FirstOrDefault();

    private static decimal NetContributions(IReadOnlyList<Flow> flows, Func<Flow, bool> window)
        => Total(flows, f => window(f) && f.Kind is FlowKind.Opening or FlowKind.Contribution)
           - Total(flows, f => window(f) && f.Kind == FlowKind.Withdrawal);

    private static decimal Total(IReadOnlyList<Flow> flows, Func<Flow, bool> which) => flows.Where(which).Sum(f => f.Amount.Amount);

    private static Money Sum(IReadOnlyList<Flow> flows, Currency currency, Func<Flow, bool> which) => M(Total(flows, which), currency);

    internal static Money M(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var money) ? money
            : throw new InvalidOperationException("A sum of four-decimal amounts has four decimals.");
}
