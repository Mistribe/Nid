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
            return new EnvelopeFigures(null, null, null, null, null, null, null, null,
                Sum(flows, currency, f => f.Kind == FlowKind.Contribution && f.OccurredOn <= d),
                Sum(flows, currency, f => f.Kind == FlowKind.Withdrawal && f.OccurredOn <= d),
                false, null);

        var v = latest.AsOf;
        var net = NetContributions(flows, f => f.OccurredOn <= v);
        var value = latest.Value.Amount;
        var gain = value - net;
        var fees = Total(flows, f => f.Kind == FlowKind.Expense && f.OccurredOn <= v);
        var missingOpening = value != 0
            && !flows.Any(f => f.OccurredOn <= v && f.Kind is FlowKind.Opening or FlowKind.Contribution);
        var threshold = StaleThreshold(cadence, staleAfterDays);
        var age = NodaTime.Period.DaysBetween(v, d);

        return new EnvelopeFigures(
            latest.Value, v,
            M(net, currency), M(gain, currency), net > 0 ? RoundPercent(gain / net * 100m) : null,
            M(fees, currency), M(gain - fees, currency),
            Sum(flows, currency, f => f.Kind == FlowKind.Income && f.OccurredOn <= v),
            Sum(flows, currency, f => f.Kind == FlowKind.Contribution && f.OccurredOn > v && f.OccurredOn <= d),
            Sum(flows, currency, f => f.Kind == FlowKind.Withdrawal && f.OccurredOn > v && f.OccurredOn <= d),
            missingOpening,
            threshold is { } limit && age > limit ? age : null);
    }

    public static PeriodPerformance Period(IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, Currency currency,
        LocalDate from, LocalDate to)
    {
        var start = LastOnOrBefore(values, from);
        var end = LastOnOrBefore(values, to);
        if (start is null || end is null)
            return new PeriodPerformance(from, to, start?.AsOf, start?.Value, end?.AsOf, end?.Value, null, null);

        var net = NetContributions(flows, f => f.OccurredOn > start.AsOf && f.OccurredOn <= end.AsOf);
        return new PeriodPerformance(from, to, start.AsOf, start.Value, end.AsOf, end.Value,
            M(net, currency), M(end.Value.Amount - start.Value.Amount - net, currency));
    }

    public static decimal RoundPercent(decimal percent) => Math.Round(percent, 1, MidpointRounding.AwayFromZero);

    public static int? StaleThreshold(ValuationCadence cadence, int? staleAfterDays) => staleAfterDays ?? cadence switch
    {
        ValuationCadence.Monthly => 45,
        ValuationCadence.Quarterly => 120,
        ValuationCadence.Yearly => 400,
        _ => null,
    };

    private static Valuation? LastOnOrBefore(IReadOnlyList<Valuation> values, LocalDate d)
        => values.Where(x => x.AsOf <= d).OrderByDescending(x => x.AsOf).FirstOrDefault();

    private static decimal NetContributions(IReadOnlyList<Flow> flows, Func<Flow, bool> window)
        => Total(flows, f => window(f) && f.Kind is FlowKind.Opening or FlowKind.Contribution)
           - Total(flows, f => window(f) && f.Kind == FlowKind.Withdrawal);

    private static decimal Total(IReadOnlyList<Flow> flows, Func<Flow, bool> which) => flows.Where(which).Sum(f => f.Amount.Amount);

    private static Money Sum(IReadOnlyList<Flow> flows, Currency currency, Func<Flow, bool> which) => M(Total(flows, which), currency);

    private static Money M(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var money) ? money
            : throw new InvalidOperationException("A sum of four-decimal amounts has four decimals.");
}
