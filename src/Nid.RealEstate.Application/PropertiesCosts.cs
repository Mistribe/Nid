using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>
/// Properties dashboard spec §5.2: what the household's properties cost, paid over the last 12 months and planned for the next.
/// <c>Property</c> restricts the answer to one property of the scope (issue #405's « par bien »); another is not found.
/// </summary>
public sealed record GetPropertiesCostsQuery(VisibilityScope Scope, ItemId? Property = null) : IQuery<PropertiesCosts>;

/// <summary>A category of expense (README §4): paid over the period, planned over the next 12 months (D14).</summary>
public sealed record CostCategoryView(string Category, LoanMoneyView? Paid, LoanMoneyView? Planned);

/// <summary>
/// Issue #405: a calendar year; complete once ended, with every property of the answer held from its 1 January and no archived
/// (sold) property of the scope held during it: spec D7 leaves those out, so their charges are missing from it.
/// </summary>
public sealed record CostYear(int Year, bool Complete);

/// <summary>A category's paid expenses in a year (null with none, or when a rate is missing), and the change from the year before,
/// a fraction with four decimals, only when both years are complete and both paid something.</summary>
public sealed record CostYearCell(LoanMoneyView? Paid, string? Change);

/// <summary>A category over <see cref="CostYears.Years"/>, one cell per year, in their order.</summary>
public sealed record CostCategoryYears(string Category, IReadOnlyList<CostYearCell> Cells);

/// <summary>Issue #405: the expenses paid per category and calendar year, the last five, the current one last.</summary>
public sealed record CostYears(IReadOnlyList<CostYear> Years, IReadOnlyList<CostCategoryYears> Categories);

/// <summary>A planned cost's occurrence in the next 12 months, in its property's currency and converted.</summary>
public sealed record CostDueView(LocalDate On, ItemId PropertyId, string Category, string Amount, string Currency, LoanMoneyView? AmountConverted);

/// <summary>
/// A property: what it paid over the period and plans a month, its rent over the period, this month's payment of its linked loans in
/// the scope (D3; null when one has no rate to its currency), and its real cost since purchase in six columns (D15), in its currency.
/// </summary>
public sealed record CostRow(
    ItemId Id, string Name, string Currency, string Paid, string PlannedMonthly, string RentReceived, string? LoanPayment,
    string Acquisition, string Works, string LoanInterest, string LoanInsurance, string LoanFees, string OtherCharges, string RealCost,
    string RentsSincePurchase, string NetCostOfRents, LoanMoneyView? RealCostConverted);

/// <summary>Spec §5.2. Every household figure is in <c>Currency</c>, null when a rate is missing (then a finding names it).</summary>
public sealed record PropertiesCosts(
    string Currency, LocalDate From, LocalDate To, LoanMoneyView? Paid, LoanMoneyView? WorksPaid, LoanMoneyView? Planned,
    LoanMoneyView? PlannedMonthly, IReadOnlyList<CostCategoryView> ByCategory, IReadOnlyList<CostDueView> Dues, IReadOnlyList<CostRow> Properties,
    LoanMoneyView? RealCost, LoanMoneyView? NetCostOfRents, CostYears ByYear, IReadOnlyList<LoanFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Properties.Select(p => p.Id);
}

[RequestHandler<GetPropertiesCostsQuery, PropertiesCosts>]
public sealed class GetPropertiesCostsHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations, IFlows flows,
    IAccessPolicy policy, IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetPropertiesCostsQuery, PropertiesCosts>
{
    public async ValueTask<Result<PropertiesCosts>> HandleAsync(GetPropertiesCostsQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(reader, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        var read = await OverviewProperties.ReadAsync(ids, reader, properties, items, loans, valuations, flows, policy, ct);
        if (read.TryGetFailure(out var failure))
            return Result.Failure<PropertiesCosts>(failure!);
        read.TryGetValue(out var found);
        var set = found!;
        if (query.Property is { } only)
        {
            set = set.Where(p => p.Item.Id == only).ToList();
            if (set.Count == 0)
                return Result.Failure<PropertiesCosts>(new NotFoundFailure("property", only.ToString()));
        }

        var reference = await household.ReferenceCurrencyAsync(ct);
        var currencies = set.Select(p => LoanViews.CurrencyOf(p.Item)).Concat(set.SelectMany(p => p.Loans).Select(l => LoanViews.CurrencyOf(l.Item))).ToHashSet();
        // The reference's own quote is needed too: rates are per USD. D4: today's rate, for the period and the year ahead alike.
        var rates = currencies.All(c => c == reference) ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. currencies, reference], today, today, ct);
        return Result.Success(PropertiesCostsMath.Build(set, reference, rates, today, query.Property is null ? await ArchivedAsync(reader, query.Scope, ids, ct) : []));
    }

    /// <summary>The scope's archived properties, from their purchase to their archiving: the years by year cannot be whole while one was held.</summary>
    private async Task<IReadOnlyList<(LocalDate Bought, LocalDate Archived)>> ArchivedAsync(PersonId reader, VisibilityScope scope, IReadOnlySet<ItemId> active, CancellationToken ct)
    {
        var gone = (await visible.ForAsync(reader, scope, includeArchived: true, ct)).Except(active).ToList();
        if (gone.Count == 0) return [];
        var found = await properties.FindManyAsync(gone, ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var zone = await calendar.ZoneAsync(ct);
        return found.Where(p => accesses.TryGetValue(p.ItemId, out var item) && item.ArchivedAt is not null)
            .Select(p => (p.Details.PurchaseDate, accesses[p.ItemId].ArchivedAt!.Value.InZone(zone).Date))
            .ToList();
    }
}

/// <summary>Spec §5's figures, pure: tested in Nid.RealEstate.Tests.</summary>
internal static class PropertiesCostsMath
{
    /// <summary>The real cost's own lines (3C1's <c>CostLine</c>); every other line is an expense category.</summary>
    private static readonly string[] LoanLines = ["loan_interest", "loan_insurance", "loan_fees"];

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public static PropertiesCosts Build(
        IReadOnlyList<OverviewProperty> set, Currency reference, RateTable rates, LocalDate today, IReadOnlyList<(LocalDate Bought, LocalDate Archived)> archived)
    {
        var first = today.PlusMonths(-12).PlusDays(1);
        var until = today.PlusMonths(12);
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

        var read = set.Select(p => (Source: p, Currency: LoanViews.CurrencyOf(p.Item), Figures: PropertiesOverviewMath.FiguresOf(p, rates, today))).ToList();
        // D14: paid is the flows of the period; planned, each running cost's occurrences in the next 12 months.
        var paid = read.SelectMany(r => r.Source.Flows
            .Where(f => f.Kind == FlowKind.Expense && f.OccurredOn >= first && f.OccurredOn <= today)
            .Select(f => (Category: f.Category ?? "other", Amount: Convert(f.Amount.Amount, r.Currency)))).ToList();
        var due = read.SelectMany(r => r.Source.Property.Costs.Select(c => c.Cost)
                .SelectMany(c => PropertyMath.DueBetween(c, today, until).Select(on => (On: on, r.Source, r.Currency, Cost: c, Converted: Convert(c.Amount, r.Currency)))))
            .OrderBy(d => d.On).ThenBy(d => d.Source.Item.DisplayName, French).ThenBy(d => d.Cost.Category, StringComparer.Ordinal)
            .ToList();
        var dues = due.Select(d => new CostDueView(d.On, d.Source.Item.Id, d.Cost.Category, Amount(d.Cost.Amount), d.Currency.Code, View(d.Converted))).ToList();
        var planned = due.Select(d => (d.Cost.Category, Amount: d.Converted)).ToList();

        var categories = paid.Select(p => p.Category).Concat(planned.Select(p => p.Category)).Distinct().ToList();
        var byCategory = categories
            .Select(c => (Category: c, Paid: SumOrNull(paid.Where(p => p.Category == c).Select(p => p.Amount)),
                Planned: SumOrNull(planned.Where(p => p.Category == c).Select(p => p.Amount))))
            .OrderByDescending(c => c.Paid ?? 0m).ThenByDescending(c => c.Planned ?? 0m).ThenBy(c => c.Category, StringComparer.Ordinal)
            .Select(c => new CostCategoryView(c.Category, View(c.Paid), View(c.Planned)))
            .ToList();

        var thisMonth = today.ToYearMonth();
        var rows = read.Select(r =>
        {
            var lines = r.Figures.RealCostLines;
            decimal Line(string code) => lines.Where(l => l.Code == code).Sum(l => l.Amount);
            var other = lines.Where(l => l.Code != "acquisition" && l.Code != PropertyCategories.Works && !LoanLines.Contains(l.Code)).Sum(l => l.Amount);
            var running = r.Source.Property.Costs.Select(c => c.Cost).Where(c => c.StartsOn <= today && (c.EndsOn is not { } end || today < end));
            var periodFlows = r.Source.Flows.Where(f => f.OccurredOn >= first && f.OccurredOn <= today).ToList();
            return (Read: r, Converted: Convert(r.Figures.RealCost, r.Currency), Row: new CostRow(r.Source.Item.Id, r.Source.Item.DisplayName, r.Currency.Code,
                Amount(periodFlows.Where(f => f.Kind == FlowKind.Expense).Sum(f => f.Amount.Amount)),
                Amount(running.Sum(PropertyMath.MonthlyEquivalent)),
                Amount(periodFlows.Where(f => f.Kind == FlowKind.Income && PropertyCategories.CashIn.Contains(f.Category!)).Sum(f => f.Amount.Amount)),
                Amount(LoanPayment(r.Source, r.Currency, rates, thisMonth, today)),
                Amount(Line("acquisition")), Amount(Line(PropertyCategories.Works)), Amount(Line("loan_interest")), Amount(Line("loan_insurance")),
                Amount(Line("loan_fees")), Amount(other), Amount(r.Figures.RealCost), Amount(r.Figures.RealCost - r.Figures.NetCostOfRents),
                Amount(r.Figures.NetCostOfRents), null));
        })
            .OrderBy(x => x.Converted is null).ThenByDescending(x => x.Converted)
            .ThenBy(x => x.Read.Source.Item.DisplayName, French).ThenBy(x => x.Read.Source.Item.Id.Value)
            .Select(x => x.Row with { RealCostConverted = View(x.Converted) })
            .ToList();

        return new PropertiesCosts(reference.Code, first, today,
            View(SumOrNull(paid.Select(p => p.Amount))), // No works among the expenses paid is 0 of works, as long as something was paid.
            View(paid.Count == 0 ? null : SumOrNull(paid.Where(p => p.Category == PropertyCategories.Works).Select(p => p.Amount).Append(0m))),
            View(SumOrNull(planned.Select(p => p.Amount))),
            // D14: the dashboard's « Charges des biens »: the monthly equivalents of the costs running today.
            View(SumOrNull(read.Select(r => Convert(r.Source.Property.Costs.Select(c => c.Cost)
                .Where(c => c.StartsOn <= today && (c.EndsOn is not { } end || today < end)).Sum(PropertyMath.MonthlyEquivalent), r.Currency)))),
            byCategory, dues, rows,
            View(SumOrNull(read.Select(r => Convert(r.Figures.RealCost, r.Currency)))),
            View(SumOrNull(read.Select(r => Convert(r.Figures.NetCostOfRents, r.Currency)))),
            ByYear(set, archived, today, Convert, View),
            missing.Values.ToList());
    }

    /// <summary>Issue #405: each expense converted alone (D4), summed per category and calendar year; works too, as « payé ».</summary>
    private static CostYears ByYear(
        IReadOnlyList<OverviewProperty> set, IReadOnlyList<(LocalDate Bought, LocalDate Archived)> archived, LocalDate today,
        Func<decimal, Currency, decimal?> convert, Func<decimal?, LoanMoneyView?> view)
    {
        var years = Enumerable.Range(today.Year - 4, 5)
            // A year counts only when every property was held from its 1 January: before or during a purchase its charges did not run all
            // year. Nor while an archived property, left out (D7), was held: its charges are missing.
            .Select(y => new CostYear(y, y < today.Year && set.All(p => p.Property.Details.PurchaseDate <= new LocalDate(y, 1, 1))
                && !archived.Any(a => a.Bought.Year <= y && a.Archived.Year >= y)))
            .ToList();
        var paid = set.SelectMany(p => p.Flows
                .Where(f => f.Kind == FlowKind.Expense && f.OccurredOn.Year >= years[0].Year && f.OccurredOn <= today)
                .Select(f => (Category: f.Category ?? "other", f.OccurredOn.Year, Amount: convert(f.Amount.Amount, LoanViews.CurrencyOf(p.Item)))))
            .ToList();
        var categories = paid.GroupBy(p => p.Category)
            .Select(g => (Category: g.Key, Sums: years.Select(y => SumOrNull(g.Where(p => p.Year == y.Year).Select(p => p.Amount))).ToList(),
                Total: g.Sum(p => p.Amount ?? 0m)))
            .OrderByDescending(c => c.Total).ThenBy(c => c.Category, StringComparer.Ordinal)
            .Select(c => new CostCategoryYears(c.Category, years.Select((y, i) => new CostYearCell(view(c.Sums[i]),
                i > 0 && y.Complete && years[i - 1].Complete && c.Sums[i - 1] is { } before and not 0m && c.Sums[i] is { } now
                    ? Math.Round((now - before) / before, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture)
                    : null)).ToList()))
            .ToList();
        return new CostYears(years, categories);
    }

    /// <summary>
    /// As the dashboard's commitments: the payment and insurance of the first row due this month of each linked loan in the scope, in
    /// the property's currency; null when a loan in another currency has no rate.
    /// </summary>
    private static decimal? LoanPayment(OverviewProperty p, Currency currency, RateTable rates, YearMonth month, LocalDate today)
    {
        decimal? total = 0m;
        foreach (var (loan, item) in p.Loans)
        {
            var loanCurrency = LoanViews.CurrencyOf(item);
            var terms = Terms(loanCurrency, loan.Contract, loan.Fees, loan.Repayments);
            if (Schedule(terms, loan).Rows.FirstOrDefault(r => r.DueDate.ToYearMonth() == month) is not { } row)
                continue;
            var due = row.Payment + row.Insurance;
            if (loanCurrency == currency) total += due;
            else if (rates.TryConvert(due, loanCurrency, currency, today, projected: false, out var converted, out _))
                total += Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            else return null;
        }
        return total;
    }

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
