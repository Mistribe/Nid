using System.Globalization;
using Nid.Core.Contracts;
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

/// <summary>An envelope of the period: its performance in its currency and converted, and its annual return since its opening (D2).</summary>
public sealed record EnvelopePeriodRow(
    ItemId Id, string Name, string Kind, string Currency, string? Performance, AmountView? PerformanceConverted, decimal? AnnualReturnPct);

/// <summary>D11: an envelope left out of the period, and why (<c>no_start_value</c>, <c>stale_start_value</c>).</summary>
public sealed record ExcludedEnvelopeView(ItemId EnvelopeId, string Name, string Code);

/// <summary>D15: the household's savings accounts together, the reference the envelopes' annual returns are read against.</summary>
public sealed record ReturnReferenceView(decimal AnnualReturnPct);

/// <summary>
/// Spec §4.2. D13: <c>EndValue = StartValue + Contributions − Withdrawals + Transfers + Performance</c>, the transfers being the legs
/// with an envelope out of the period. Every figure in <c>Currency</c> at the rate of <c>To</c>; null when a rate is missing.
/// </summary>
public sealed record InvestmentsPerformance(
    string Currency, LocalDate From, LocalDate To,
    AmountView? StartValue, AmountView? EndValue, AmountView? Contributions, AmountView? Withdrawals, AmountView? Transfers,
    AmountView? Performance, decimal? PerformancePct,
    IReadOnlyList<MonthPerformanceView> Months, string? BestMonth, string? WorstMonth,
    IReadOnlyList<EnvelopePeriodRow> Envelopes, IReadOnlyList<ExcludedEnvelopeView> Excluded, ReturnReferenceView? Reference,
    IReadOnlyList<OverviewFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Envelopes.Select(e => e.Id).Concat(Excluded.Select(e => e.EnvelopeId));
}

public static class PerformanceCodes
{
    public const string NoStartValue = "no_start_value";
    public const string StaleStartValue = "stale_start_value";
}

[RequestHandler<GetInvestmentsPerformanceQuery, InvestmentsPerformance>]
public sealed class GetInvestmentsPerformanceHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHouseholdCalendar calendar,
    IActorAccessor actor, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetInvestmentsPerformanceQuery, InvestmentsPerformance>
{
    public async ValueTask<Result<InvestmentsPerformance>> HandleAsync(GetInvestmentsPerformanceQuery query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if (query.From >= query.To || query.To > today)
            return Result.Failure<InvestmentsPerformance>(new Failure(InvestmentsCodes.InvalidPeriod,
                "The period's start must be before its end, and its end no later than today.", null));

        var reader = actor.Current!.PersonId!.Value;
        // D9: a closed envelope counts before its closing.
        var ids = await visible.ForAsync(reader, query.Scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var block = await performance.FactsManyAsync(found, accesses, ct);
        var facts = new List<EnvelopePerformance.Facts>();
        foreach (var envelope in found)
        {
            EnvelopePerformance.Facts? f;
            if (block is not null) block.TryGetValue(envelope.ItemId, out f);
            else (await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out f);
            if (f is not null) facts.Add(f);
        }

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = facts.Select(f => f.Currency).Where(c => c != reference).Distinct().ToList();
        // The period's end and today (the reference's annual return): rates are per USD, the reference's own quote too.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], query.To, today, ct);
        return Result.Success(InvestmentsPerformanceMath.Build(facts, reference, rates, today, query.From, query.To));
    }
}

/// <summary>Spec §4's figures, pure: tested in Nid.Investments.Tests.</summary>
internal static class InvestmentsPerformanceMath
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    private sealed record Counted(EnvelopePerformance.Facts Facts, PeriodPerformance Period);

    public static InvestmentsPerformance Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, Currency reference, RateTable rates, LocalDate today, LocalDate from, LocalDate to)
    {
        var convert = new Converter(reference, rates);
        decimal? At(decimal amount, Currency currency) => convert.Of(amount, currency, to);

        var counted = new List<Counted>();
        var excluded = new List<ExcludedEnvelopeView>();
        // An envelope closed by the start has nothing in the period.
        foreach (var f in facts.Where(f => f.Envelope.ClosedOn is not { } closed || closed > from))
        {
            var period = EnvelopeMath.Period(f.Values, f.Flows, f.Currency, from, to);
            var limit = EnvelopeMath.StaleThreshold(f.Envelope.Cadence, f.Envelope.StaleAfterDays);
            if (period.StartAsOf is not { } start)
                excluded.Add(new ExcludedEnvelopeView(f.Item.Id, f.Item.DisplayName, PerformanceCodes.NoStartValue));
            else if (limit is { } days && Period.DaysBetween(start, from) > days)
                excluded.Add(new ExcludedEnvelopeView(f.Item.Id, f.Item.DisplayName, PerformanceCodes.StaleStartValue));
            else
                counted.Add(new Counted(f, period));
        }

        // D13: the flows each envelope's performance counts, between its two values.
        decimal Window(Counted c, Func<Flow, bool> which)
            => c.Facts.Flows.Where(f => f.OccurredOn > c.Period.StartAsOf && f.OccurredOn <= c.Period.EndAsOf && which(f)).Sum(f => f.Amount.Amount);
        decimal? Household(Func<Counted, decimal> amount) => SumOrNull(counted.Select(c => At(amount(c), c.Facts.Currency)));

        var startValue = Household(c => c.Period.StartValue!.Value.Amount);
        var endValue = Household(c => c.Period.EndValue!.Value.Amount);
        var contributions = Household(c => Window(c, f => f.Kind is FlowKind.Opening or FlowKind.Contribution && !IsTransfer(f)));
        var withdrawals = Household(c => Window(c, f => f.Kind == FlowKind.Withdrawal && !IsTransfer(f)));
        var transfers = Household(c => Window(c, f => f.Kind is FlowKind.Opening or FlowKind.Contribution && IsTransfer(f))
            - Window(c, f => f.Kind == FlowKind.Withdrawal && IsTransfer(f)));
        var performance = Household(c => c.Period.Performance!.Value.Amount);
        var engaged = startValue is { } s && contributions is { } paid ? s + paid : (decimal?)null;

        var months = Months(counted, from, to, At);
        var known = months.Where(m => m.Amount is not null).ToList();
        var rows = counted
            .Select(c => (c.Facts, Amount: c.Period.Performance!.Value, Converted: At(c.Period.Performance.Value.Amount, c.Facts.Currency)))
            .OrderBy(x => x.Converted is null).ThenByDescending(x => x.Converted).ThenBy(x => x.Facts.Item.DisplayName, French).ThenBy(x => x.Facts.Item.Id.Value)
            .Select(x => new EnvelopePeriodRow(x.Facts.Item.Id, x.Facts.Item.DisplayName, EnvelopeWire.Of(x.Facts.Envelope.Kind), x.Facts.Currency.Code,
                x.Amount.Amount.ToString("0.00", CultureInfo.InvariantCulture), convert.View(x.Converted),
                EnvelopeMath.At(x.Facts.Values, x.Facts.Flows, x.Facts.Currency, x.Facts.Envelope.Cadence, x.Facts.Envelope.StaleAfterDays, today).AnnualReturnPct))
            .ToList();

        return new InvestmentsPerformance(reference.Code, from, to,
            convert.View(startValue), convert.View(endValue), convert.View(contributions), convert.View(withdrawals), convert.View(transfers),
            convert.View(performance),
            performance is { } p && engaged is { } e && e > 0m ? EnvelopeMath.RoundPercent(p / e * 100m) : null,
            months.Select(m => new MonthPerformanceView(m.Month, convert.View(m.Amount))).ToList(),
            known.Count >= 2 ? known.MaxBy(m => m.Amount)!.Month : null,
            known.Count >= 2 ? known.MinBy(m => m.Amount)!.Month : null,
            rows, excluded.OrderBy(x => x.Name, French).ThenBy(x => x.EnvelopeId.Value).ToList(),
            Reference(facts, convert, today), convert.Missing);
    }

    /// <summary>
    /// D12: each calendar month of the period, from the end of the month before (or <paramref name="from"/>) to its end (or
    /// <paramref name="to"/>); a month of no length is left out. The performance is additive, so the months add up to the period.
    /// </summary>
    private static List<(string Month, decimal? Amount)> Months(
        IReadOnlyList<Counted> counted, LocalDate from, LocalDate to, Func<decimal, Currency, decimal?> at)
    {
        var months = new List<(string, decimal?)>();
        for (var month = from.ToYearMonth(); month <= to.ToYearMonth(); month = month.PlusMonths(1))
        {
            var start = month == from.ToYearMonth() ? from : month.PlusMonths(-1).ToDateInterval().End;
            var end = month == to.ToYearMonth() ? to : month.ToDateInterval().End;
            if (start >= end) continue;
            var amount = SumOrNull(counted.Select(c =>
                EnvelopeMath.Period(c.Facts.Values, c.Facts.Flows, c.Facts.Currency, start, end).Performance is { } p ? at(p.Amount, c.Facts.Currency) : 0m));
            months.Add((month.ToString("yyyy-MM", CultureInfo.InvariantCulture), amount ?? (counted.Count == 0 ? 0m : null)));
        }
        return months;
    }

    /// <summary>D15: the open savings accounts with a value, together (the household's annual return, D6, on that kind alone).</summary>
    private static ReturnReferenceView? Reference(IReadOnlyList<EnvelopePerformance.Facts> facts, Converter convert, LocalDate today)
    {
        var savings = facts.Where(f => f.Item.ArchivedAt is null && f.Envelope.Kind == EnvelopeKind.Savings)
            .Select(f => (Facts: f, Figures: EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today)))
            .Where(x => x.Figures.Value is not null)
            .Select(x => new Valued(x.Facts, x.Figures, convert.Of(x.Figures.Value!.Value.Amount, x.Facts.Currency, today), null))
            .ToList();
        return AnnualReturn(savings, convert, today) is { } pct ? new ReturnReferenceView(pct) : null;
    }
}
