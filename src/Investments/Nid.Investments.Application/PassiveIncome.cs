using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Investments.Application.InvestmentsOverviewMath;

namespace Nid.Investments.Application;

/// <summary>Passive income spec §3.2: the capital's income of the 12 months to today, in the Foyer / Moi scope.</summary>
public sealed record GetPassiveIncomeQuery(VisibilityScope Scope) : IQuery<PassiveIncome>;

/// <summary>Passive income spec C1: the kinds of income, in the order every list of them follows.</summary>
public static class PassiveIncomeKinds
{
    public const string Dividend = "dividend", Interest = "interest", Distribution = "distribution", Rent = "rent", Other = "other";

    public static readonly IReadOnlyList<string> All = [Dividend, Interest, Distribution, Rent, Other];
}

/// <summary>Spec D3: why a savings account is left out.</summary>
public static class PassiveIncomeCodes
{
    public const string BankLinkedSavings = "bank_linked_savings", SavingsWithoutFlows = "savings_without_flows";
}

/// <summary>One kind of income and its amount; a list of them holds the five, always, in <see cref="PassiveIncomeKinds.All"/>'s order.</summary>
public sealed record IncomeKindView(string Kind, AmountView? Amount);

/// <summary>A month of the window (<c>yyyy-MM</c>); the current one is <c>Partial</c>.</summary>
public sealed record IncomeMonthView(string Month, bool Partial, AmountView? Total, IReadOnlyList<IncomeKindView> ByKind);

/// <summary>An envelope or a property with income: <c>Source</c> is <c>envelope</c> or <c>property</c>, <c>Kind</c> the envelope's kind or <c>property</c>.</summary>
public sealed record IncomeRowView(string Source, ItemId Id, string Name, string Kind,
    AmountView? Total, IReadOnlyList<IncomeKindView> ByKind, decimal? YieldPct);

/// <summary>Spec C5: the next 12 months, the window's income of the sources still open today.</summary>
public sealed record IncomeEstimateView(AmountView? Total, IReadOnlyList<IncomeKindView> ByKind);

/// <summary>An envelope left out of the income, and why (<see cref="PassiveIncomeCodes"/>).</summary>
public sealed record ExcludedEnvelopeView(ItemId EnvelopeId, string Name, string Code);

/// <summary>
/// Spec §3.2: the household's income of the 12 months to <c>To</c>. Each (row, month, kind) amount is converted alone at today's rate (C8);
/// every total adds them, null when one is missing (then a finding names the currency).
/// </summary>
public sealed record PassiveIncome(string Currency, LocalDate From, LocalDate To,
    AmountView? Total, decimal? YieldPct, IReadOnlyList<IncomeKindView> ByKind, IReadOnlyList<IncomeMonthView> Months,
    IReadOnlyList<IncomeRowView> Rows, IncomeEstimateView Estimate, IReadOnlyList<ExcludedEnvelopeView> Excluded,
    IReadOnlyList<OverviewFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Rows.Select(r => r.Id).Concat(Excluded.Select(e => e.EnvelopeId));
}

/// <summary>Spec D3–D5, C1, C3–C8, pure: tested in Nid.Investments.Tests.</summary>
internal static class PassiveIncomeMath
{
    /// <summary>An envelope or a property: its values (in its currency) at the window's month ends, null where unknown.</summary>
    private sealed record Source(string Origin, ItemId Id, string Name, string Kind, Currency Currency, bool OpenToday, IReadOnlyList<decimal?> Values);

    private sealed record Cell(Source Source, YearMonth Month, string Kind, decimal? Amount);

    public static LocalDate WindowStart(LocalDate today) => new LocalDate(today.Year, today.Month, 1).PlusMonths(-11);

    /// <summary>The overview's 12-month grid without its first date: the end of each month of the window, today for the current one.</summary>
    public static IReadOnlyList<LocalDate> MonthEnds(LocalDate today) => Grid(OverviewSpan.TwelveMonths, today).Skip(1).ToList();

    public static PassiveIncome Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, IReadOnlyList<EnvelopeHolding> holdings, IReadOnlyList<HoldingMovement> movements,
        IReadOnlyList<PropertyIncomeSource> properties, Currency reference, RateTable rates, LocalDate today)
    {
        var convert = new Converter(reference, rates);
        var from = WindowStart(today);
        // The end of the month before the window, then each month end: month i runs from bounds[i] to bounds[i + 1].
        var bounds = Grid(OverviewSpan.TwelveMonths, today);
        var ends = bounds.Skip(1).ToList();
        bool InWindow(LocalDate d) => d >= from && d <= today;

        var entries = new List<(Source Source, LocalDate On, string Kind, decimal Amount)>();
        var excluded = new List<ExcludedEnvelopeView>();
        var envelopes = new Dictionary<ItemId, Source>();
        foreach (var f in facts)
        {
            var savings = f.Envelope.Kind == EnvelopeKind.Savings;
            if (savings && f.Envelope.ClosedOn is { } closed && closed < from) continue;
            string? code = !savings ? null
                : f.Values.Any(v => v.Source == FactSource.BankApi && InWindow(v.AsOf)) ? PassiveIncomeCodes.BankLinkedSavings
                : !f.Flows.Any(x => x.Kind is FlowKind.Opening or FlowKind.Contribution) ? PassiveIncomeCodes.SavingsWithoutFlows
                : null;
            if (code is not null)
            {
                excluded.Add(new ExcludedEnvelopeView(f.Item.Id, f.Item.DisplayName, code));
                continue;
            }

            var source = new Source("envelope", f.Item.Id, f.Item.DisplayName, EnvelopeWire.Of(f.Envelope.Kind), f.Currency,
                f.Envelope.ClosedOn is null && f.Item.ArchivedAt is null,
                ends.Select(d => f.Values.Where(v => v.AsOf <= d).MaxBy(v => v.AsOf)?.Value.Amount).ToList());
            envelopes[f.Item.Id] = source;
            // D3: a savings account's withdrawals and income flows are in its performance already.
            foreach (var x in f.Flows.Where(x => !savings && InWindow(x.OccurredOn)))
            {
                if (x.Kind == FlowKind.Withdrawal && FlowCategories.IsIncome(x.Category))
                    entries.Add((source, x.OccurredOn, x.Category!, x.Amount.Amount));
                else if (x.Kind == FlowKind.Income)
                    entries.Add((source, x.OccurredOn, FlowCategories.IsIncome(x.Category) ? x.Category! : PassiveIncomeKinds.Other, x.Amount.Amount));
            }
            if (savings)
                for (var i = 0; i < ends.Count; i++)
                    // A window without a value at its start earns no interest: counted from 0, its whole first value would.
                    entries.Add((source, ends[i], PassiveIncomeKinds.Interest,
                        EnvelopeMath.Period(f.Values, f.Flows, f.Currency, bounds[i], bounds[i + 1]) is { StartAsOf: not null, Performance: { } p } ? p.Amount : 0m));
        }

        var holdingKinds = holdings.ToDictionary(h => h.Id, h => h.Kind);
        foreach (var m in movements.Where(m => InWindow(m.OccurredOn) && m.Kind is MovementKinds.Interest or MovementKinds.Income))
            // D3: a savings account's movements are in its valuations, so in its performance already.
            if (envelopes.TryGetValue(m.Envelope, out var source) && source.Kind != EnvelopeWire.Of(EnvelopeKind.Savings))
                entries.Add((source, m.OccurredOn,
                    m.Kind == MovementKinds.Interest ? PassiveIncomeKinds.Interest
                    : holdingKinds.GetValueOrDefault(m.HoldingId) is "equity" or "etf" ? PassiveIncomeKinds.Dividend
                    : PassiveIncomeKinds.Distribution, m.Amount));

        foreach (var p in properties)
        {
            var source = new Source("property", p.Item, p.Name, "property", p.Currency, !p.Archived && p.LetToday,
                ends.Select(d => p.ValueAt.GetValueOrDefault(d)).ToList());
            foreach (var month in p.Months.Where(x => InWindow(x.Month)))
                entries.Add((source, month.Month, PassiveIncomeKinds.Rent, month.Rent - month.Costs));
        }

        // C8: each (row, month, kind) converted alone, so every total is the sum of the amounts shown.
        var cells = entries.Where(e => e.Amount != 0m)
            .GroupBy(e => (e.Source.Id, Month: e.On.ToYearMonth(), e.Kind))
            .Select(g => (Source: g.First().Source, g.Key.Month, g.Key.Kind, Amount: g.Sum(e => e.Amount)))
            .Select(x => new Cell(x.Source, x.Month, x.Kind, convert.Of(x.Amount, x.Source.Currency, today)))
            .ToList();

        var rows = cells.GroupBy(c => c.Source.Id)
            .Select(g => (Source: g.First().Source, Cells: g.ToList(), Total: Sum(g), Average: Average(g.First().Source, convert, today)))
            .OrderBy(r => r.Total is null).ThenByDescending(r => r.Total)
            .ThenBy(r => r.Source.Name, InvestmentsPerformanceMath.French).ThenBy(r => r.Source.Id.Value)
            .ToList();
        var total = Sum(cells);
        // Spec C6: the household's yield over the rows with a known value; an unvalued row would only inflate it.
        var valued = rows.Where(r => r.Source.Values.Any(v => v is not null)).ToList();

        var months = ends.Select((end, i) => new IncomeMonthView(end.ToYearMonth().ToString("yyyy-MM", CultureInfo.InvariantCulture), i == ends.Count - 1,
                convert.View(Sum(cells.Where(c => c.Month == end.ToYearMonth()))), ByKind(cells.Where(c => c.Month == end.ToYearMonth()), convert)))
            .ToList();
        var open = cells.Where(c => c.Source.OpenToday).ToList();

        return new PassiveIncome(reference.Code, from, today,
            convert.View(total), Yield(SumOrNull(valued.Select(r => r.Total)), SumOrNull(valued.Select(r => r.Average))), ByKind(cells, convert), months,
            rows.Select(r => new IncomeRowView(r.Source.Origin, r.Source.Id, r.Source.Name, r.Source.Kind,
                convert.View(r.Total), ByKind(r.Cells, convert), Yield(r.Total, r.Average))).ToList(),
            new IncomeEstimateView(convert.View(Sum(open)), ByKind(open, convert)),
            excluded.OrderBy(x => x.Name, InvestmentsPerformanceMath.French).ThenBy(x => x.EnvelopeId.Value).ToList(),
            convert.Missing);
    }

    /// <summary>Nothing to sum is no income, 0; one amount missing makes the sum unknown.</summary>
    private static decimal? Sum(IEnumerable<Cell> cells) => SumOrNull(cells.Select(c => c.Amount).Prepend(0m));

    private static List<IncomeKindView> ByKind(IEnumerable<Cell> cells, Converter convert)
        => PassiveIncomeKinds.All.Select(k => new IncomeKindView(k, convert.View(Sum(cells.Where(c => c.Kind == k))))).ToList();

    /// <summary>Spec C6: the mean of the values known at the month ends, each converted at today's rate; null with none or one missing.</summary>
    private static decimal? Average(Source source, Converter convert, LocalDate today)
    {
        var known = source.Values.OfType<decimal>().ToList();
        return SumOrNull(known.Select(v => convert.Of(v, source.Currency, today))) is { } sum ? sum / known.Count : null;
    }

    private static decimal? Yield(decimal? income, decimal? average)
        => income is { } i && average is { } a && a > 0m ? EnvelopeMath.RoundPercent(i / a * 100m) : null;
}

[RequestHandler<GetPassiveIncomeQuery, PassiveIncome>]
public sealed class GetPassiveIncomeHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHoldingStore holdings,
    IPropertyIncome propertyIncome, IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetPassiveIncomeQuery, PassiveIncome>
{
    public async ValueTask<Result<PassiveIncome>> HandleAsync(GetPassiveIncomeQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        // D9: a closed envelope still earned before its closing.
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

        var export = await holdings.ExportAsync(facts.Select(f => f.Item.Id).ToList(), ct);
        // The range starts on a month start (its first month would be partial otherwise); its end is exclusive.
        var range = DateRange.Create(PassiveIncomeMath.WindowStart(today), today.PlusDays(1)).TryGetValue(out var window) ? window : throw new InvalidOperationException();
        var read = await propertyIncome.IncomeAsync(reader, query.Scope, range, PassiveIncomeMath.MonthEnds(today), today, ct);
        if (!read.TryGet(out var properties, out var failure)) return Result.Failure<PassiveIncome>(failure);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = facts.Select(f => f.Currency).Concat(properties.Select(p => p.Currency)).Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return Result.Success(PassiveIncomeMath.Build(facts, export.Holdings, export.Movements, properties, reference, rates, today));
    }
}
