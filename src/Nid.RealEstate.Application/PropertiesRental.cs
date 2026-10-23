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

/// <summary>Properties dashboard spec §4.2: the let properties over the 12 months to today; <c>Property</c> keeps one of them.</summary>
public sealed record GetPropertiesRentalQuery(VisibilityScope Scope, ItemId? Property) : IQuery<PropertiesRental>;

/// <summary>One calendar month of the period (D11), its first and last ones partial; the cash flow is D12's.</summary>
public sealed record RentalMonthView(string Month, LoanMoneyView? RentReceived, LoanMoneyView? Charges, LoanMoneyView? LoanPayments, LoanMoneyView? CashFlow);

public sealed record CurrentTenancyView(string Label, LocalDate StartsOn, LocalDate? EndsOn);

public sealed record RentalTenancyView(ItemId PropertyId, string Label, LocalDate StartsOn, LocalDate? EndsOn);

/// <summary>
/// A let property: its rental figures as its page's (D2), in its currency; its charges, loan payments (a loan in another currency
/// converted at today's rate, null when the rate is missing) and cash flow over the period, and its cash flow converted.
/// </summary>
public sealed record RentalRow(
    ItemId Id, string Name, string Currency, CurrentTenancyView? CurrentTenancy, string? RentMonthly, string ExpectedRent, string RentReceived,
    string Arrears, string? VacancyRate, string? GrossYield, string? NetYieldBeforeTax, string Charges, string? LoanPayments, string? CashFlow,
    LoanMoneyView? CashFlowConverted);

/// <summary>Spec §4.2. Every household figure is in <c>Currency</c>, null when a rate is missing (then a finding names it).</summary>
public sealed record PropertiesRental(
    string Currency, LocalDate From, LocalDate To, int Count, LoanMoneyView? ExpectedRent, LoanMoneyView? RentReceived, LoanMoneyView? Arrears,
    int? VacantDays, string? VacancyRate, string? GrossYield, string? NetYieldBeforeTax, int NotSignificantCount, LoanMoneyView? Charges,
    LoanMoneyView? LoanPayments, LoanMoneyView? CashFlow, LoanMoneyView? CashFlowMonthly, IReadOnlyList<RentalMonthView> Months,
    IReadOnlyList<RentalRow> Properties, IReadOnlyList<RentalTenancyView> Tenancies, IReadOnlyList<LoanFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Properties.Select(p => p.Id);
}

[RequestHandler<GetPropertiesRentalQuery, PropertiesRental>]
public sealed class GetPropertiesRentalHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations, IFlows flows,
    IAccessPolicy policy, IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetPropertiesRentalQuery, PropertiesRental>
{
    public async ValueTask<Result<PropertiesRental>> HandleAsync(GetPropertiesRentalQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(reader, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        var read = await OverviewProperties.ReadAsync(ids, reader, properties, items, loans, valuations, flows, policy, ct);
        if (read.TryGetFailure(out var failure))
            return Result.Failure<PropertiesRental>(failure!);
        read.TryGetValue(out var found);
        var set = found!;

        var reference = await household.ReferenceCurrencyAsync(ct);
        var currencies = set.Select(p => LoanViews.CurrencyOf(p.Item)).Concat(set.SelectMany(p => p.Loans).Select(l => LoanViews.CurrencyOf(l.Item))).ToHashSet();
        // The reference's own quote is needed too: rates are per USD. D4: a sum over the period at its last day's rate.
        var rates = currencies.All(c => c == reference) ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. currencies, reference], today, today, ct);
        var rental = PropertiesRentalMath.Build(set, reference, rates, today, query.Property);
        // A property that is not one of the reader's let properties in the scope is not found, whatever the reason.
        return rental is null && query.Property is { } property
            ? Result.Failure<PropertiesRental>(new NotFoundFailure("property", property.ToString()))
            : Result.Success(rental!);
    }
}

/// <summary>Spec §4's figures, pure: tested in Nid.RealEstate.Tests.</summary>
internal static class PropertiesRentalMath
{
    /// <summary>Spec §4.2: the leases of the last three years on the timeline.</summary>
    private const int TimelineYears = 3;

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>A let property's amounts in its currency, month by month in the period.</summary>
    private sealed record Let(OverviewProperty Source, Currency Currency, PropertyFigures Figures, LocalDate From,
        IReadOnlyList<(decimal Rent, decimal Charges, decimal ChargesButWorks, decimal? Loans)> Months);

    /// <returns>Null when <paramref name="only"/> names no let property of the set.</returns>
    public static PropertiesRental? Build(IReadOnlyList<OverviewProperty> set, Currency reference, RateTable rates, LocalDate today, ItemId? only = null)
    {
        var first = today.PlusMonths(-12).PlusDays(1);
        var months = new List<YearMonth>();
        for (var m = first.ToYearMonth(); m <= today.ToYearMonth(); m = m.PlusMonths(1))
            months.Add(m);
        var missing = new SortedDictionary<string, LoanFindingView>(StringComparer.Ordinal);
        decimal? Convert(decimal? amount, Currency from)
        {
            if (amount is not { } a) return null;
            if (from == reference || a == 0m) return a;
            if (rates.TryConvert(a, from, reference, today, projected: false, out var converted, out var miss))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            var key = $"{from.Code}:{LocalDatePattern.Iso.Format(today)}";
            missing.TryAdd(key, new LoanFindingView(FxCodes.RateMissing,
                $"No {miss.Currency.Code} rate within {rates.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}.", key));
            return null;
        }
        LoanMoneyView? View(decimal? amount) => amount is { } a ? new LoanMoneyView(Amount(a), reference.Code) : null;

        // D10: the properties whose rental start is known and past.
        var let = set
            .Select(p => (Source: p, Currency: LoanViews.CurrencyOf(p.Item), Figures: PropertiesOverviewMath.FiguresOf(p, rates, today)))
            .Where(x => x.Figures.Rental.RentedSince is { } since && since <= today)
            .Where(x => only is not { } id || x.Source.Item.Id == id)
            .Select(x => new Let(x.Source, x.Currency, x.Figures, x.Figures.Rental.RentedSince!.Value > first ? x.Figures.Rental.RentedSince.Value : first,
                months.Select(m => Month(x.Source, x.Currency, rates, m, first, today)).ToList()))
            .ToList();
        if (only is not null && let.Count == 0)
            return null;

        decimal? Household(Func<Let, decimal?> amount) => SumOrNull(let.Select(l => Convert(amount(l), l.Currency)));
        var monthViews = months.Select((m, i) =>
        {
            var rent = Household(l => l.Months[i].Rent);
            var charges = Household(l => l.Months[i].Charges);
            var loans = Household(l => l.Months[i].Loans);
            var cash = Household(l => Cash(l.Months[i]));
            return (Month: m, Rent: rent, Charges: charges, Loans: loans, Cash: cash);
        }).ToList();
        var rentTotal = SumOrNull(monthViews.Select(m => m.Rent));
        var chargesTotal = SumOrNull(monthViews.Select(m => m.Charges));
        var loansTotal = SumOrNull(monthViews.Select(m => m.Loans));
        var cashTotal = SumOrNull(monthViews.Select(m => m.Cash));

        // D13: the vacant days over the days each property's window counts.
        var vacant = let.Sum(l => l.Figures.Rental.VacantDays ?? 0);
        var days = let.Sum(l => Period.DaysBetween(l.From, today) + 1);
        // D9: on the properties whose yield is significant, their rent and charges but works over their cost price.
        var significant = let.Where(l => l.Figures.Rental.GrossYield is not null).ToList();
        var yieldRent = SumOrNull(significant.Select(l => Convert(l.Months.Sum(m => m.Rent), l.Currency)));
        var yieldCharges = SumOrNull(significant.Select(l => Convert(l.Months.Sum(m => m.ChargesButWorks), l.Currency)));
        var yieldCost = SumOrNull(significant.Select(l => Convert(l.Figures.CostPrice, l.Currency)));

        var rows = let
            .Select(l => (Let: l, Rent: Convert(l.Months.Sum(m => m.Rent), l.Currency)))
            .OrderBy(x => x.Rent is null).ThenByDescending(x => x.Rent)
            .ThenBy(x => x.Let.Source.Item.DisplayName, French).ThenBy(x => x.Let.Source.Item.Id.Value)
            .Select(x => Row(x.Let, today, Convert(SumOrNull(x.Let.Months.Select(Cash)), x.Let.Currency) is { } c ? View(c) : null))
            .ToList();
        var since = today.PlusYears(-TimelineYears);
        var tenancies = let
            .SelectMany(l => l.Source.Property.Tenancies.Select(t => t.Tenancy)
                .Where(t => t.EndsOn is not { } ends || ends >= since)
                .Select(t => new RentalTenancyView(l.Source.Item.Id, t.Label, t.StartsOn, t.EndsOn)))
            .ToList();

        return new PropertiesRental(reference.Code, first, today, let.Count,
            View(Household(l => l.Figures.Rental.ExpectedRent)), View(rentTotal), View(Household(l => l.Figures.Rental.Arrears)),
            let.Count == 0 ? null : vacant, days > 0 ? Fraction((decimal)vacant / days) : null,
            yieldRent is { } yr && yieldCost is { } yc && yc > 0m ? Fraction(yr / yc) : null,
            yieldRent is { } nr && yieldCharges is { } nc && yieldCost is { } ncost && ncost > 0m ? Fraction((nr - nc) / ncost) : null,
            let.Count - significant.Count, View(chargesTotal), View(loansTotal), View(cashTotal),
            View(cashTotal is { } c ? Math.Round(c / 12m, 2, MidpointRounding.AwayFromZero) : null),
            monthViews.Select(m => new RentalMonthView(m.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                View(m.Rent), View(m.Charges), View(m.Loans), View(m.Cash))).ToList(),
            rows, tenancies, missing.Values.ToList());
    }

    /// <summary>
    /// D12: a month of a property within ]today − 12 months, today]: the rent received, every expense (works too: it is cash), the
    /// expenses but works (the net yield's), and the linked loans' payments due in it, insurance included, in the property's currency.
    /// </summary>
    private static (decimal Rent, decimal Charges, decimal ChargesButWorks, decimal? Loans) Month(
        OverviewProperty p, Currency currency, RateTable rates, YearMonth month, LocalDate first, LocalDate today)
    {
        var monthStart = month.OnDayOfMonth(1);
        var monthEnd = monthStart.PlusMonths(1).PlusDays(-1);
        var start = monthStart < first ? first : monthStart;
        var end = monthEnd > today ? today : monthEnd;
        var flows = p.Flows.Where(f => f.OccurredOn >= start && f.OccurredOn <= end).ToList();
        var rent = flows.Where(f => f.Kind == FlowKind.Income && PropertyCategories.CashIn.Contains(f.Category!)).Sum(f => f.Amount.Amount);
        var expenses = flows.Where(f => f.Kind == FlowKind.Expense).ToList();
        decimal? loans = 0m;
        foreach (var (loan, item) in p.Loans)
        {
            var loanCurrency = LoanViews.CurrencyOf(item);
            var terms = Terms(loanCurrency, loan.Contract, loan.Fees, loan.Repayments);
            var due = Schedule(terms, loan).Rows.Where(r => r.DueDate >= start && r.DueDate <= end).Sum(r => r.Payment + r.Insurance);
            if (due == 0m) continue;
            if (loanCurrency == currency) loans += due;
            else if (rates.TryConvert(due, loanCurrency, currency, today, projected: false, out var converted, out _))
                loans += Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            else loans = null; // the property's own fx_missing warning names the loan
        }
        return (rent, expenses.Sum(f => f.Amount.Amount), expenses.Where(f => f.Category != PropertyCategories.Works).Sum(f => f.Amount.Amount), loans);
    }

    private static decimal? Cash((decimal Rent, decimal Charges, decimal ChargesButWorks, decimal? Loans) m) => m.Rent - m.Charges - m.Loans;

    private static RentalRow Row(Let l, LocalDate today, LoanMoneyView? cashConverted)
    {
        var r = l.Figures.Rental;
        var current = l.Source.Property.Tenancies.Select(t => t.Tenancy).FirstOrDefault(t => PropertyMath.Covers(t, today));
        var loans = SumOrNull(l.Months.Select(m => m.Loans));
        var cash = SumOrNull(l.Months.Select(Cash));
        return new RentalRow(l.Source.Item.Id, l.Source.Item.DisplayName, l.Currency.Code,
            current is null ? null : new CurrentTenancyView(current.Label, current.StartsOn, current.EndsOn),
            current is null ? null : Amount(current.RentMonthly + current.ChargesMonthly),
            Amount(r.ExpectedRent), Amount(l.Months.Sum(m => m.Rent)), Amount(r.Arrears), Fraction(r.VacancyRate), Fraction(r.GrossYield),
            Fraction(r.NetYieldBeforeTax), Amount(l.Months.Sum(m => m.Charges)), Amount(loans), Amount(cash), cashConverted);
    }

    private static string? Fraction(decimal? x) => x is { } f ? Math.Round(f, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture) : null;

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
