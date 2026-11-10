using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// One period rule for the investments (the Performance page, the yearly review): what a reader sees over [from, to], closed envelopes
/// included (D9), the rates from <c>from</c> to today, and, when <c>to</c> is today, the envelopes' estimates as the net worth history.
/// The period is the caller's to check.
/// </summary>
public sealed class InvestmentsPeriodReader(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx, EnvelopeEstimator estimator)
{
    internal async Task<Result<PeriodFigures>> ReadAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        // Batched reads spec §3: a block that cannot be read falls back to each envelope's own read.
        var block = await performance.FactsManyAsync(found, accesses, ct);
        var facts = new List<EnvelopePerformance.Facts>();
        foreach (var envelope in found)
        {
            EnvelopePerformance.Facts? f;
            if (block is not null) block.TryGetValue(envelope.ItemId, out f);
            else (await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out f);
            if (f is not null) facts.Add(f);
        }

        var today = await calendar.TodayAsync(ct);
        var estimates = to == today
            ? await estimator.EstimateAsync(found.Select(e => e.ItemId).ToList(), ct)
            : null;
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = facts.Select(f => f.Currency).Where(c => c != reference).Distinct().ToList();
        // The period's start and end, and today (the reference's annual return): rates are per USD, the reference's own quote too.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], from, today, ct);
        return Result.Success(InvestmentsPerformanceMath.Build(facts, reference, rates, today, from, to, estimates));
    }
}

/// <summary>
/// A period's investment figures in the reference currency, before any view (see <see cref="InvestmentsPerformanceMath.Build"/>); each
/// reader of the period projects them its own way.
/// </summary>
internal sealed record PeriodFigures(
    Currency Reference, LocalDate From, LocalDate To,
    decimal? StartValue, decimal? EndValue, decimal? Contributions, decimal? Withdrawals, decimal? Transfers, decimal? Performance,
    decimal? PerformancePct, decimal? PeriodReturnPct,
    IReadOnlyList<PeriodFigures.MonthFigure> Months, string? BestMonth, string? WorstMonth,
    IReadOnlyList<PeriodFigures.Row> Envelopes, decimal? ReferenceReturnPct, IReadOnlyList<OverviewFindingView> Findings)
{
    /// <summary>D12: a month's performance, at the rate of the period's end.</summary>
    internal sealed record MonthFigure(string Month, decimal? Performance);

    /// <summary>An envelope counted: <c>Performance</c> in its currency, the other amounts converted.</summary>
    internal sealed record Row(
        EnvelopePerformance.Facts Facts, decimal Performance, decimal? StartValue, decimal? EndValue, decimal? NetContributions,
        decimal? PerformanceConverted, decimal? AnnualReturnPct, decimal? PeriodReturnPct, bool StaleStart);

    /// <summary>The Performance page's view (spec §4.2).</summary>
    public InvestmentsPerformance ToView()
        => new(Reference.Code, From, To,
            View(StartValue), View(EndValue), View(Contributions), View(Withdrawals), View(Transfers), View(Performance), PerformancePct, PeriodReturnPct,
            Months.Select(m => new MonthPerformanceView(m.Month, View(m.Performance))).ToList(), BestMonth, WorstMonth,
            Envelopes.Select(r => new EnvelopePeriodRow(r.Facts.Item.Id, r.Facts.Item.DisplayName, EnvelopeWire.Of(r.Facts.Envelope.Kind),
                r.Facts.Currency.Code, r.Performance.ToString("0.00", CultureInfo.InvariantCulture), View(r.PerformanceConverted), r.AnnualReturnPct,
                View(r.StartValue), View(r.EndValue), View(r.NetContributions), r.PeriodReturnPct, r.StaleStart)).ToList(),
            ReferenceReturnPct is { } pct ? new ReturnReferenceView(pct) : null, Findings);

    /// <summary>Issue #12: the same figures for the yearly review.</summary>
    public InvestmentsReviewPart ToReviewPart()
        => new(StartValue, EndValue, Contributions, Withdrawals, Performance, PeriodReturnPct,
            Envelopes.Select(r => new InvestmentsReviewRow(r.Facts.Item.Id, r.Facts.Item.DisplayName, EnvelopeWire.Of(r.Facts.Envelope.Kind),
                r.StartValue, r.EndValue, r.NetContributions, r.PerformanceConverted, r.PeriodReturnPct, r.StaleStart)).ToList(),
            Findings.Select(f => f.Code).ToList());

    private AmountView? View(decimal? amount)
        => amount is { } a ? new AmountView(a.ToString("0.00", CultureInfo.InvariantCulture), Reference.Code) : null;
}
