using NodaTime;

namespace Nid.Core.Domain.Tax;

public enum CapitalFlowKind { Payment, Withdrawal, NeutralTransferIn, NeutralTransferOut }

/// <summary><c>TransferredBefore2017</c> / <c>TransferredAfter2017</c>: on a neutral transfer in, the capital the source gave up, by period (D16).</summary>
public sealed record CapitalFlow(
    LocalDate On, CapitalFlowKind Kind, decimal Amount, decimal? TransferredBefore2017 = null, decimal? TransferredAfter2017 = null);

public sealed record ValuePoint(LocalDate On, decimal Value);

/// <summary>Exact, unrounded amounts; <c>TransferredOut</c> is the capital each neutral transfer out carried, by period, in date order.</summary>
public sealed record RemainingCapitalResult(
    decimal Before2017, decimal After2017, bool Approximate, IReadOnlyList<(decimal Before2017, decimal After2017)> TransferredOut);

/// <summary>Sale tax spec §4.4 (D2): the premiums still in a life insurance contract or a PEA, each past withdrawal taken as capital and gain pro rata of the value just before it.</summary>
public static class RemainingCapital
{
    // art. 125-0 A CGI: the 7.5 % / 12.8 % regime covers premiums « versées à compter du 27 septembre 2017 ».
    private static readonly LocalDate PremiumCutoff = new(2017, 9, 27);
    private const int MaxValueAgeDays = 90;

    private static bool IsIn(CapitalFlow flow) => flow.Kind is CapitalFlowKind.Payment or CapitalFlowKind.NeutralTransferIn;

    /// <summary>
    /// The caller passes the opening balance as the earliest Payment: <paramref name="premiumsBefore2017AtStart"/> applies to it only.
    /// A contract whose first capital is a NeutralTransferIn ignores it, its split coming from the transfer.
    /// </summary>
    public static RemainingCapitalResult Replay(
        decimal premiumsBefore2017AtStart, IReadOnlyList<CapitalFlow> flows, IReadOnlyList<ValuePoint> values, LocalDate until)
    {
        // Same-day flows: money in before money out, so a withdrawal sees that day's payments in the value.
        var ordered = flows.Where(f => f.On <= until).OrderBy(f => f.On).ThenBy(f => IsIn(f) ? 0 : 1).ToList();
        var firstIn = ordered.FindIndex(IsIn);
        var opening = firstIn >= 0 && ordered[firstIn].Kind == CapitalFlowKind.Payment ? firstIn : -1;
        decimal before = 0m, after = 0m;
        var approximate = false;
        var transferredOut = new List<(decimal, decimal)>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var flow = ordered[i];
            if (flow.Kind == CapitalFlowKind.NeutralTransferIn)
            {
                // D16: a neutral transfer (PACTE) brings the source's remaining capital with its premiums' dates, not its value.
                if (flow.TransferredBefore2017 is null && flow.TransferredAfter2017 is null)
                {
                    approximate = true;
                    after += flow.Amount;
                }
                else (before, after) = (before + (flow.TransferredBefore2017 ?? 0m), after + (flow.TransferredAfter2017 ?? 0m));
                continue;
            }
            if (flow.Kind == CapitalFlowKind.Payment)
            {
                if (i == opening)
                {
                    // D8b: the opening amount holds the declared pre-2017 premiums, the rest is post-2017 whatever its date.
                    var pre = Math.Min(premiumsBefore2017AtStart, flow.Amount);
                    (before, after) = (before + pre, after + flow.Amount - pre);
                }
                else if (flow.On < PremiumCutoff) before += flow.Amount;
                else after += flow.Amount;
                continue;
            }

            var point = values.Where(v => v.On <= flow.On).MaxBy(v => v.On);
            var value = 0m;
            if (point is not null)
            {
                approximate |= Period.DaysBetween(point.On, flow.On) > MaxValueAgeDays;
                // A value is the end of its day: on its own date it already holds this withdrawal and the ones after it that day
                // (that day's payments come first), so they are added back.
                value = point.On == flow.On
                    ? point.Value + ordered.Skip(i).Where(f => f.On == flow.On && !IsIn(f)).Sum(f => f.Amount)
                    : point.Value + ordered.Take(i).Where(f => f.On > point.On).Sum(f => IsIn(f) ? f.Amount : -f.Amount);
            }
            if (value <= 0m)
            {
                // §4.4: no usable value, V(d) is taken as the remaining capital (the withdrawal is all capital).
                approximate = true;
                value = before + after;
            }

            var (outBefore, outAfter) = flow.Amount >= value
                ? (before, after)
                : (before * flow.Amount / value, after * flow.Amount / value);
            (before, after) = (before - outBefore, after - outAfter);
            if (flow.Kind == CapitalFlowKind.NeutralTransferOut) transferredOut.Add((outBefore, outAfter));
        }

        return new RemainingCapitalResult(before, after, approximate, transferredOut);
    }
}
