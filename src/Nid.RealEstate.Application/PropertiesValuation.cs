using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>Properties dashboard spec §6.2: how the household's properties are valued against what they cost.</summary>
public sealed record GetPropertiesValuationQuery(VisibilityScope Scope) : IQuery<PropertiesValuation>;

public sealed record EstimateView(LocalDate On, string Value);

/// <summary>D18: the cost price from that date on (the purchase, then each works).</summary>
public sealed record CostStepView(LocalDate On, string CostPrice);

/// <summary>A property in its currency: its value and cost price as its page's (D2), its price per m² (D16) and annual pace (D17), and its history (D18).</summary>
public sealed record ValuationRow(
    ItemId Id, string Name, string Type, string Currency, LocalDate PurchaseDate, string? SurfaceM2, string? Value, LocalDate? ValueOn, bool Stale,
    string Cadence, string CostPrice, string? LatentGain, decimal? LatentGainPct, string? ValuePerM2, decimal? AnnualPacePct,
    IReadOnlyList<EstimateView> Estimates, IReadOnlyList<CostStepView> CostSteps, LoanMoneyView? ValueConverted);

/// <summary>Spec §6.2. Every household figure is in <c>Currency</c>, null when a rate is missing (then a finding names it).</summary>
public sealed record PropertiesValuation(
    string Currency, int Count, LoanMoneyView? Value, LoanMoneyView? CostPrice, LoanMoneyView? LatentGain, decimal? LatentGainPct,
    string? SurfaceM2, LoanMoneyView? ValuePerM2, int WithoutSurfaceCount, int UpToDateCount, int StaleCount, int WithoutValueCount,
    IReadOnlyList<ValuationRow> Properties, IReadOnlyList<LoanFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Properties.Select(p => p.Id);
}

[RequestHandler<GetPropertiesValuationQuery, PropertiesValuation>]
public sealed class GetPropertiesValuationHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations, IFlows flows,
    IAccessPolicy policy, IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetPropertiesValuationQuery, PropertiesValuation>
{
    public async ValueTask<Result<PropertiesValuation>> HandleAsync(GetPropertiesValuationQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(reader, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        var read = await OverviewProperties.ReadAsync(ids, reader, properties, items, loans, valuations, flows, policy, ct);
        if (read.TryGetFailure(out var failure))
            return Result.Failure<PropertiesValuation>(failure!);
        read.TryGetValue(out var found);
        var set = found!;

        var reference = await household.ReferenceCurrencyAsync(ct);
        var currencies = set.Select(p => LoanViews.CurrencyOf(p.Item)).Concat(set.SelectMany(p => p.Loans).Select(l => LoanViews.CurrencyOf(l.Item))).ToHashSet();
        // The reference's own quote is needed too: rates are per USD. D4: today's figures at today's rate.
        var rates = currencies.All(c => c == reference) ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. currencies, reference], today, today, ct);
        return Result.Success(PropertiesValuationMath.Build(set, reference, rates, today));
    }
}

/// <summary>Spec §6's figures, pure: tested in Nid.RealEstate.Tests.</summary>
internal static class PropertiesValuationMath
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public static PropertiesValuation Build(IReadOnlyList<OverviewProperty> set, Currency reference, RateTable rates, LocalDate today)
    {
        var missing = new SortedDictionary<string, LoanFindingView>(StringComparer.Ordinal);
        decimal? Convert(decimal amount, Currency from)
        {
            if (from == reference || amount == 0m) return amount;
            if (rates.TryConvert(amount, from, reference, today, projected: false, out var converted, out var miss))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            var key = $"{from.Code}:{LocalDatePattern.Iso.Format(today)}";
            missing.TryAdd(key, new LoanFindingView(FxCodes.RateMissing,
                $"No {miss.Currency.Code} rate within {rates.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}.", key));
            return null;
        }
        LoanMoneyView? View(decimal? amount) => amount is { } a ? new LoanMoneyView(Amount(a), reference.Code) : null;

        var read = set.Select(p => (Source: p, Currency: LoanViews.CurrencyOf(p.Item), Figures: PropertiesOverviewMath.FiguresOf(p, rates, today)))
            .Select(r => (r.Source, r.Currency, r.Figures, Value: r.Figures.Value is { } v ? Convert(v, r.Currency) : null))
            .ToList();
        // As the overview: the properties with an estimate.
        var valued = read.Where(r => r.Figures.Value is not null).ToList();
        var value = SumOrNull(valued.Select(r => r.Value));
        var cost = SumOrNull(valued.Select(r => Convert(r.Figures.CostPrice, r.Currency)));
        var gain = value is { } v1 && cost is { } c1 ? v1 - c1 : (decimal?)null;
        // D16: over the properties with a surface and an estimate.
        var sized = valued.Where(r => r.Source.Property.Details.SurfaceM2 is > 0m).ToList();
        var surface = sized.Count == 0 ? (decimal?)null : sized.Sum(r => r.Source.Property.Details.SurfaceM2!.Value);
        var sizedValue = SumOrNull(sized.Select(r => r.Value));

        var rows = read
            .OrderBy(r => r.Figures.Value is null ? 2 : r.Value is null ? 1 : 0).ThenByDescending(r => r.Value)
            .ThenBy(r => r.Source.Item.DisplayName, French).ThenBy(r => r.Source.Item.Id.Value)
            .Select(r => Row(r.Source, r.Currency, r.Figures, today, View(r.Value)))
            .ToList();

        return new PropertiesValuation(reference.Code, read.Count, View(value), View(cost), View(gain), Pct(gain, cost),
            surface is { } s ? Amount(s) : null,
            View(sizedValue is { } sv && surface is { } sf ? Math.Round(sv / sf, 0, MidpointRounding.AwayFromZero) : null),
            valued.Count - sized.Count, valued.Count(r => !r.Figures.Stale), valued.Count(r => r.Figures.Stale), read.Count - valued.Count,
            rows, missing.Values.ToList());
    }

    private static ValuationRow Row(OverviewProperty p, Currency currency, PropertyFigures f, LocalDate today, LoanMoneyView? valueConverted)
    {
        var d = p.Property.Details;
        var perM2 = f.Value is { } v && d.SurfaceM2 is { } s and > 0m ? Math.Round(v / s, 0, MidpointRounding.AwayFromZero) : (decimal?)null;
        return new ValuationRow(p.Item.Id, p.Item.DisplayName, LoanWire.Of(d.Type), currency.Code, d.PurchaseDate, Amount(d.SurfaceM2), Amount(f.Value),
            f.ValueOn, f.Stale, LoanWire.Of(d.Cadence), Amount(f.CostPrice), Amount(f.LatentGain), Pct(f.LatentGain, f.CostPrice), Amount(perM2),
            AnnualPace(f.Value, f.CostPrice, d.PurchaseDate, today),
            p.Values.Where(e => e.AsOf <= today).OrderBy(e => e.AsOf).Select(e => new EstimateView(e.AsOf, Amount(e.Value.Amount))).ToList(),
            CostSteps(p, today), valueConverted);
    }

    /// <summary>
    /// D17: (value ÷ cost price)^(365.25 ÷ days held) − 1, in % to 0.1; null without an estimate or before a year held. A power of a
    /// decimal has no exact form: computed in double, as the annual returns, then rounded.
    /// </summary>
    internal static decimal? AnnualPace(decimal? value, decimal costPrice, LocalDate purchased, LocalDate today)
    {
        var days = Period.DaysBetween(purchased, today);
        if (value is not { } v || v <= 0m || costPrice <= 0m || days < 365) return null;
        var pace = Math.Pow((double)(v / costPrice), 365.25 / days) - 1;
        return Math.Round((decimal)pace * 100m, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>D18: the cost price from the purchase on, then after each date of works (several the same day are one step).</summary>
    private static List<CostStepView> CostSteps(OverviewProperty p, LocalDate today)
    {
        var d = p.Property.Details;
        var costPrice = d.PurchasePrice + d.NotaryFees + d.AgencyFees + d.OtherAcquisitionCosts;
        var steps = new List<CostStepView> { new(d.PurchaseDate, Amount(costPrice)) };
        foreach (var day in p.Flows.Where(f => f.Kind == FlowKind.Expense && f.Category == PropertyCategories.Works && f.OccurredOn <= today)
                     .GroupBy(f => f.OccurredOn).OrderBy(g => g.Key))
        {
            costPrice += day.Sum(f => f.Amount.Amount);
            steps.Add(new CostStepView(day.Key, Amount(costPrice)));
        }
        return steps;
    }

    /// <summary>A part of a positive total in %, 0.1 %.</summary>
    private static decimal? Pct(decimal? part, decimal? total)
        => part is { } p && total is { } t && t > 0m ? Math.Round(p / t * 100m, 1, MidpointRounding.AwayFromZero) : null;

    /// <summary>Null with nothing to sum, or when one amount is unknown.</summary>
    private static decimal? SumOrNull(IEnumerable<decimal?> amounts)
    {
        decimal? total = null;
        foreach (var a in amounts)
        {
            if (a is not { } value) return null;
            total = (total ?? 0m) + value;
        }
        return total;
    }
}
