using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Investments.Application.InvestmentsOverviewMath;

namespace Nid.Investments.Application;

/// <summary>Investments dashboard spec §4.2: the household's performance over [from, to], in the Foyer / Moi scope.</summary>
public sealed record GetInvestmentsPerformanceQuery(VisibilityScope Scope, LocalDate From, LocalDate To) : IQuery<InvestmentsPerformance>;

/// <summary>D12: one calendar month of the period, its envelopes' performances converted at the period's end (D4).</summary>
public sealed record MonthPerformanceView(string Month, AmountView? Performance);

/// <summary>
/// An envelope of the period: its performance in its currency and converted, its values and net contributions converted (the start at
/// the rate of <c>From</c>, the rest at the rate of <c>To</c>), its money-weighted return over the period, and its annual return since its
/// opening (D2). <c>StaleStart</c>: its value at the start is older than its staleness threshold.
/// </summary>
public sealed record EnvelopePeriodRow(
    ItemId Id, string Name, string Kind, string Currency, string? Performance, AmountView? PerformanceConverted, decimal? AnnualReturnPct,
    AmountView? StartValue, AmountView? EndValue, AmountView? NetContributions, decimal? PeriodReturnPct, bool StaleStart);

/// <summary>D15: the household's savings accounts together, the reference the envelopes' annual returns are read against.</summary>
public sealed record ReturnReferenceView(decimal AnnualReturnPct);

/// <summary>
/// Spec §4.2. D13: <c>EndValue = StartValue + Contributions − Withdrawals + Transfers + Performance</c>, the transfers being the legs
/// with an envelope out of the period. Every figure in <c>Currency</c>, the start value at the rate of <c>From</c> and the rest at the
/// rate of <c>To</c>; null when a rate is missing. An envelope without a value at the start counts from 0.
/// </summary>
public sealed record InvestmentsPerformance(
    string Currency, LocalDate From, LocalDate To,
    AmountView? StartValue, AmountView? EndValue, AmountView? Contributions, AmountView? Withdrawals, AmountView? Transfers,
    AmountView? Performance, decimal? PerformancePct, decimal? PeriodReturnPct,
    IReadOnlyList<MonthPerformanceView> Months, string? BestMonth, string? WorstMonth,
    IReadOnlyList<EnvelopePeriodRow> Envelopes, ReturnReferenceView? Reference,
    IReadOnlyList<OverviewFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Envelopes.Select(e => e.Id);
}

[RequestHandler<GetInvestmentsPerformanceQuery, InvestmentsPerformance>]
public sealed class GetInvestmentsPerformanceHandler(InvestmentsPeriodReader period, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<GetInvestmentsPerformanceQuery, InvestmentsPerformance>
{
    public async ValueTask<Result<InvestmentsPerformance>> HandleAsync(GetInvestmentsPerformanceQuery query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if (query.From >= query.To || query.To > today)
            return Result.Failure<InvestmentsPerformance>(new Failure(InvestmentsCodes.InvalidPeriod,
                "The period's start must be before its end, and its end no later than today.", null));

        return (await period.ReadAsync(actor.Current!.PersonId!.Value, query.Scope, query.From, query.To, ct)).Map(figures => figures.ToView());
    }
}

/// <summary>Spec §4's figures, pure: tested in Nid.Investments.Tests.</summary>
internal static class InvestmentsPerformanceMath
{
    internal static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>An envelope of the period; <c>End</c> its value at the end, today's estimate when <see cref="TodayValue"/> picks it.</summary>
    private sealed record Counted(EnvelopePerformance.Facts Facts, PeriodPerformance Period, decimal End, bool StaleStart)
    {
        public decimal Start => Period.StartValue!.Value.Amount;

        public decimal Net => Period.NetContributions!.Value.Amount;

        /// <summary>D13: the flows its performance counts, after its start value; without one, every flow up to its end value.</summary>
        public IEnumerable<Flow> Window
            => Facts.Flows.Where(f => (Period.StartAsOf is not { } start || f.OccurredOn > start) && f.OccurredOn <= Period.EndAsOf);

        /// <summary>
        /// Its period return's cash flows: the start value at <paramref name="from"/> (<c>AtStart</c>), the money in and out (a flow before
        /// <paramref name="from"/> was in at the start), the end value at <paramref name="to"/>.
        /// </summary>
        public IEnumerable<(LocalDate On, decimal Amount, bool AtStart)> Cash(LocalDate from, LocalDate to)
            => Window.Where(f => f.Kind is FlowKind.Opening or FlowKind.Contribution or FlowKind.Withdrawal)
                .Select(f => (LocalDate.Max(f.OccurredOn, from), f.Kind == FlowKind.Withdrawal ? f.Amount.Amount : -f.Amount.Amount, false))
                .Prepend((from, -Start, true))
                .Append((to, End, false));
    }

    /// <param name="estimates">The envelopes' estimates (holding prices spec §5.4), read when <paramref name="to"/> is today for an envelope still open today.</param>
    public static PeriodFigures Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, Currency reference, RateTable rates, LocalDate today, LocalDate from, LocalDate to,
        IReadOnlyDictionary<ItemId, Prices.EnvelopeEstimate>? estimates = null)
    {
        var convert = new Converter(reference, rates);
        decimal? At(decimal amount, Currency currency) => convert.Of(amount, currency, to);
        decimal? AtStart(decimal amount, Currency currency) => convert.Of(amount, currency, from);

        var counted = new List<Counted>();
        // An envelope closed by the start has nothing in the period; one without a value by the end, nothing to count.
        foreach (var f in facts.Where(f => f.Envelope.ClosedOn is not { } closed || closed > from))
        {
            var period = EnvelopeMath.Period(f.Values, f.Flows, f.Currency, from, to);
            if (period.EndValue is not { } end) continue;
            var limit = EnvelopeMath.StaleThreshold(f.Envelope.Cadence, f.Envelope.StaleAfterDays);
            var stale = period.StartAsOf is { } start && limit is { } days && Period.DaysBetween(start, from) > days;
            var value = to == today && (f.Envelope.ClosedOn is not { } closedOn || today < closedOn) && estimates?.GetValueOrDefault(f.Item.Id) is { } estimate
                ? TodayValue.Pick(EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today), estimate).Value.Amount
                : end.Amount;
            counted.Add(new Counted(f, period, value, stale));
        }

        decimal Window(Counted c, Func<Flow, bool> which) => c.Window.Where(which).Sum(f => f.Amount.Amount);
        decimal? Household(Func<Counted, decimal?> amount) => SumOrNull(counted.Select(amount));

        var startValue = Household(c => AtStart(c.Start, c.Facts.Currency));
        var endValue = Household(c => At(c.End, c.Facts.Currency));
        var contributions = Household(c => At(Window(c, f => f.Kind is FlowKind.Opening or FlowKind.Contribution && !IsTransfer(f)), c.Facts.Currency));
        var withdrawals = Household(c => At(Window(c, f => f.Kind == FlowKind.Withdrawal && !IsTransfer(f)), c.Facts.Currency));
        var transfers = Household(c => At(Window(c, f => f.Kind is FlowKind.Opening or FlowKind.Contribution && IsTransfer(f))
            - Window(c, f => f.Kind == FlowKind.Withdrawal && IsTransfer(f)), c.Facts.Currency));
        // D13 in converted terms: the start and the end are not at the same rate.
        var performance = endValue - startValue - contributions + withdrawals - transfers;
        var engaged = startValue + contributions;
        // Converted as the figures; one that cannot convert leaves the return unknown.
        var cash = counted.SelectMany(c => c.Cash(from, to).Select(x => (x.On, Amount: x.AtStart ? AtStart(x.Amount, c.Facts.Currency) : At(x.Amount, c.Facts.Currency))))
            .ToList();

        var months = Months(counted, from, to, At);
        var known = months.Where(m => m.Performance is not null).ToList();
        var rows = counted
            .Select(c =>
            {
                var (start, end, net) = (AtStart(c.Start, c.Facts.Currency), At(c.End, c.Facts.Currency), At(c.Net, c.Facts.Currency));
                return new PeriodFigures.Row(c.Facts, c.End - c.Start - c.Net, start, end, net, end - start - net,
                    EnvelopeMath.At(c.Facts.Values, c.Facts.Flows, c.Facts.Currency, c.Facts.Envelope.Cadence, c.Facts.Envelope.StaleAfterDays, today).AnnualReturnPct,
                    Xirr.PeriodPercent(c.Cash(from, to).Select(x => (x.On, x.Amount)), from, to), c.StaleStart);
            })
            .OrderBy(r => r.PerformanceConverted is null).ThenByDescending(r => r.PerformanceConverted)
            .ThenBy(r => r.Facts.Item.DisplayName, French).ThenBy(r => r.Facts.Item.Id.Value)
            .ToList();

        return new PeriodFigures(reference, from, to, startValue, endValue, contributions, withdrawals, transfers, performance,
            performance is { } p && engaged is { } e && e > 0m ? EnvelopeMath.RoundPercent(p / e * 100m) : null,
            cash.All(x => x.Amount is not null) ? Xirr.PeriodPercent(cash.Select(x => (x.On, x.Amount!.Value)), from, to) : null,
            months,
            known.Count >= 2 ? known.MaxBy(m => m.Performance)!.Month : null,
            known.Count >= 2 ? known.MinBy(m => m.Performance)!.Month : null,
            rows, Reference(facts, convert, today), convert.Missing);
    }

    /// <summary>
    /// D12: each calendar month of the period, from the end of the month before (or <paramref name="from"/>) to its end (or
    /// <paramref name="to"/>); a month of no length is left out. The performance is additive, so the months add up to the period.
    /// </summary>
    private static List<PeriodFigures.MonthFigure> Months(
        IReadOnlyList<Counted> counted, LocalDate from, LocalDate to, Func<decimal, Currency, decimal?> at)
    {
        var months = new List<PeriodFigures.MonthFigure>();
        for (var month = from.ToYearMonth(); month <= to.ToYearMonth(); month = month.PlusMonths(1))
        {
            var start = month == from.ToYearMonth() ? from : month.PlusMonths(-1).ToDateInterval().End;
            var end = month == to.ToYearMonth() ? to : month.ToDateInterval().End;
            if (start >= end) continue;
            // The period's last month carries today's estimate over the end value.
            var amount = SumOrNull(counted.Select(c =>
                EnvelopeMath.Period(c.Facts.Values, c.Facts.Flows, c.Facts.Currency, start, end).Performance is { } p
                    ? at(p.Amount + (end == to ? c.End - c.Period.EndValue!.Value.Amount : 0m), c.Facts.Currency) : 0m));
            months.Add(new(month.ToString("yyyy-MM", CultureInfo.InvariantCulture), amount ?? (counted.Count == 0 ? 0m : null)));
        }
        return months;
    }

    /// <summary>D15: the open savings accounts with a value, together (the household's annual return, D6, on that kind alone).</summary>
    private static decimal? Reference(IReadOnlyList<EnvelopePerformance.Facts> facts, Converter convert, LocalDate today)
    {
        var savings = facts.Where(f => f.Item.ArchivedAt is null && f.Envelope.Kind == EnvelopeKind.Savings)
            .Select(f => (Facts: f, Figures: EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today)))
            .Where(x => x.Figures.Value is not null)
            .Select(x => new Valued(x.Facts, x.Figures, convert.Of(x.Figures.Value!.Value.Amount, x.Facts.Currency, today), null))
            .ToList();
        return AnnualReturn(savings, convert, today);
    }
}
