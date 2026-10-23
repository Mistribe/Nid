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

public enum PropertiesSpan { TwelveMonths, ThreeYears, All }

/// <summary>Properties dashboard spec §3.2: the household's properties at a glance, in the Foyer / Moi scope (D6).</summary>
public sealed record GetPropertiesOverviewQuery(VisibilityScope Scope, PropertiesSpan Span) : IQuery<PropertiesOverview>;

/// <summary>A type of property (D8) and its share of the value, 0.1 %.</summary>
public sealed record PropertyTypeShareView(string Type, LoanMoneyView? Value, decimal? SharePct);

/// <summary>D5: a person's part of each property and of each linked loan, each by its own shares; <c>SharePct</c> of the net value.</summary>
public sealed record PersonPropertyShareView(Guid PersonId, LoanMoneyView? Value, LoanMoneyView? LinkedBalance, LoanMoneyView? NetValue, decimal? SharePct);

/// <summary>A property: its figures in its currency, as its detail's (D2), and its value and net value converted (D4).</summary>
public sealed record PropertyOverviewRow(
    ItemId Id, string Name, string Type, string Currency, IReadOnlyList<ItemOwnerView> Owners, string? Value, LocalDate? ValueOn, bool Stale,
    string LinkedBalance, string? NetValue, string? LatentGain, string? GrossYield, LoanMoneyView? ValueConverted, LoanMoneyView? NetValueConverted);

/// <summary>One point of the curve: the estimates and the linked loans' balances on that date, converted at it (D4).</summary>
public sealed record PropertyPointView(LocalDate On, LoanMoneyView? Value, LoanMoneyView? LinkedBalance, LoanMoneyView? NetValue);

/// <summary>Spec §3.2: something to look at; the screen writes the sentence from the code.</summary>
public sealed record PropertyWatchView(string Code, ItemId PropertyId, ItemId? LoanId, LocalDate? On, LoanMoneyView? Amount);

/// <summary>Spec §3.2. Every household figure is in <c>Currency</c>, null when a rate is missing (then a finding names it).</summary>
public sealed record PropertiesOverview(
    string Currency, int Count, LoanMoneyView? Value, LocalDate? OldestAsOf, int StaleCount, int WithoutValueCount,
    LoanMoneyView? LinkedBalance, int LoanCount, LoanMoneyView? NetValue, decimal? NetValuePct,
    LoanMoneyView? CostPrice, LoanMoneyView? LatentGain, decimal? LatentGainPct, LoanMoneyView? RentReceived, string? GrossYield,
    IReadOnlyList<PropertyTypeShareView> ByType, IReadOnlyList<PersonPropertyShareView> ByPerson, IReadOnlyList<PropertyOverviewRow> Properties,
    IReadOnlyList<PropertyPointView> Points, IReadOnlyList<PropertyWatchView> Watch, IReadOnlyList<LoanFindingView> Findings) : IExposesItems
{
    // A watched loan is a linked loan the reader can read (D3): named with its property.
    public IEnumerable<ItemId> ExposedItems() => Properties.Select(p => p.Id).Concat(Watch.Select(w => w.LoanId).OfType<ItemId>()).Distinct();
}

public static class PropertyOverviewCodes
{
    public const string RentArrears = "rent_arrears";

    /// <summary>Rental spec §3.3: on the anniversary (<c>revision_lost</c>: the previous one), <c>revision_due</c> with the new rent.</summary>
    public const string RevisionDue = "revision_due";
    public const string RevisionLost = "revision_lost";
    public const string IrlMissing = "irl_missing";
    public const string RevisionForbiddenDpe = "revision_forbidden_dpe";
}

/// <summary>
/// A visible property as the overview reads it: its facts, its linked loans visible in the scope (D3), and the private ones the
/// reader can read but the scope leaves out (<c>visibility_mismatch</c>).
/// </summary>
internal sealed record OverviewProperty(
    ItemAccess Item, StoredProperty Property, IReadOnlyList<Valuation> Values, IReadOnlyList<Flow> Flows,
    IReadOnlyList<(StoredLoan Loan, ItemAccess Item)> Loans, IReadOnlyList<ItemId> LeftOut);

[RequestHandler<GetPropertiesOverviewQuery, PropertiesOverview>]
public sealed class GetPropertiesOverviewHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, ILoanStore loans, IValuations valuations, IFlows flows,
    IAccessPolicy policy, IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx, IPersonRegistry persons,
    IIrlStore irl)
    : IRequestHandler<GetPropertiesOverviewQuery, PropertiesOverview>
{
    public async ValueTask<Result<PropertiesOverview>> HandleAsync(GetPropertiesOverviewQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(reader, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);

        var read = await OverviewProperties.ReadAsync(ids, reader, properties, items, loans, valuations, flows, policy, ct);
        if (read.TryGetFailure(out var failure))
            return Result.Failure<PropertiesOverview>(failure!);
        read.TryGetValue(out var found);
        var set = found!;

        var grid = PropertiesOverviewMath.Grid(query.Span, today);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var currencies = set.Select(p => LoanViews.CurrencyOf(p.Item)).Concat(set.SelectMany(p => p.Loans).Select(l => LoanViews.CurrencyOf(l.Item))).ToHashSet();
        // The reference's own quote is needed too: rates are per USD. The loader reaches back MaxGapDays before the first date.
        var rates = currencies.All(c => c == reference) ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. currencies, reference], grid[0], today, ct);
        return Result.Success(PropertiesOverviewMath.Build(set, reference, rates, today, grid, query.Span, await persons.ListAsync(ct),
            await irl.ListAsync(ct)));
    }
}

/// <summary>What the overview's tabs read alike: the visible properties, their facts, and their linked loans as D3 counts them.</summary>
internal static class OverviewProperties
{
    /// <summary>
    /// Batched reads spec §3, as the list: the items, the facts as one block, the linked loans and their items, each read once. A
    /// loan counts when it is visible in the scope (D3); a private one the reader can read but the scope leaves out is only named.
    /// </summary>
    public static async Task<Result<IReadOnlyList<OverviewProperty>>> ReadAsync(
        IReadOnlySet<ItemId> ids, PersonId reader, IPropertyStore properties, IItemRegistry items, ILoanStore loans, IValuations valuations,
        IFlows flows, IAccessPolicy policy, CancellationToken ct)
    {
        var found = await properties.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var listed = found.Where(p => accesses.ContainsKey(p.ItemId)).ToList();
        var propertyIds = listed.Select(p => p.ItemId).ToList();
        var series = await valuations.SeriesManyAsync(propertyIds, PropertyReading.Everything, ct);
        if (series.TryGetFailure(out var failure))
            return Result.Failure<IReadOnlyList<OverviewProperty>>(failure!);
        var listedFlows = await flows.ListManyAsync(propertyIds, PropertyReading.Everything, null, ct);
        if (listedFlows.TryGetFailure(out failure))
            return Result.Failure<IReadOnlyList<OverviewProperty>>(failure!);
        series.TryGetValue(out var values);
        listedFlows.TryGetValue(out var flowLists);

        var linked = await loans.FindLinkedAsync(propertyIds, ct);
        var outside = linked.Where(l => !ids.Contains(l.ItemId)).Select(l => l.ItemId).ToList();
        var readableOutside = await policy.CheckManyAsync(reader, outside, AccessKind.Read, ct);
        var loanItems = await items.FindManyAsync(linked.Where(l => ids.Contains(l.ItemId) || readableOutside.Contains(l.ItemId)).Select(l => l.ItemId).ToList(), ct);
        return Result.Success<IReadOnlyList<OverviewProperty>>(listed.Select(p =>
        {
            var own = linked.Where(l => l.PropertyId == p.ItemId && loanItems.ContainsKey(l.ItemId)).ToList();
            return new OverviewProperty(accesses[p.ItemId], p, values![p.ItemId], flowLists![p.ItemId],
                own.Where(l => ids.Contains(l.ItemId)).Select(l => (l, loanItems[l.ItemId])).ToList(),
                own.Where(l => !ids.Contains(l.ItemId) && loanItems[l.ItemId].Visibility == Visibility.Private).Select(l => l.ItemId).ToList());
        }).ToList());
    }
}

/// <summary>Spec §3.2's figures, pure: tested in Nid.RealEstate.Tests.</summary>
internal static class PropertiesOverviewMath
{
    /// <summary>As « À venir » of the dashboard.</summary>
    public const int UpcomingDays = 60;

    private const int MaxYears = 20;

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>As the net worth curve (2D2b): the last day of each of the N months before today's, then today.</summary>
    public static IReadOnlyList<LocalDate> Grid(PropertiesSpan span, LocalDate today)
    {
        var months = span switch { PropertiesSpan.TwelveMonths => 12, PropertiesSpan.ThreeYears => 36, _ => MaxYears * 12 };
        var firstOfMonth = new LocalDate(today.Year, today.Month, 1);
        var dates = new List<LocalDate>();
        for (var i = months; i >= 1; i--)
            dates.Add(firstOfMonth.PlusMonths(1 - i).PlusDays(-1));
        dates.Add(today);
        return dates;
    }

    /// <summary>Each amount converted alone at its date and rounded to the cent, so a total is the sum of the lines shown (D4).</summary>
    private sealed class Converter(Currency reference, RateTable rates)
    {
        private readonly SortedDictionary<string, LoanFindingView> missing = new(StringComparer.Ordinal);

        public IReadOnlyList<LoanFindingView> Missing => missing.Values.ToList();

        public decimal? Of(decimal amount, Currency from, LocalDate on)
        {
            if (from == reference || amount == 0m) return Math.Round(amount, 2, MidpointRounding.AwayFromZero); // zero converts to zero at any rate
            if (rates.TryConvert(amount, from, reference, on, projected: false, out var converted, out var miss))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            var key = $"{from.Code}:{LocalDatePattern.Iso.Format(on)}";
            missing.TryAdd(key, new LoanFindingView(FxCodes.RateMissing,
                $"No {miss.Currency.Code} rate within {rates.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}.", key));
            return null;
        }

        public LoanMoneyView? View(decimal? amount) => amount is { } a ? new LoanMoneyView(Amount(a), reference.Code) : null;
    }

    /// <summary>A property read once at today: its figures (D2) and its amounts converted.</summary>
    private sealed record Read(OverviewProperty Source, Currency Currency, PropertyFigures Figures, decimal? Value, decimal? CostPrice);

    /// <summary>A linked loan in the scope, read once: its terms and schedule for the curve, its balance today converted.</summary>
    private sealed record Owed(ItemAccess Item, Currency Currency, LoanTerms Terms, LoanSchedule Schedule, decimal Balance, decimal? Converted);

    /// <param name="persons">The household in creation order, for the order of <c>ByPerson</c>.</param>
    /// <param name="irl">The stored IRL series, for the rent revisions' findings.</param>
    public static PropertiesOverview Build(
        IReadOnlyList<OverviewProperty> set, Currency reference, RateTable rates, LocalDate today, IReadOnlyList<LocalDate> grid,
        PropertiesSpan span, IReadOnlyList<PersonInfo> persons, IReadOnlyList<IrlValue> irl)
    {
        var convert = new Converter(reference, rates);
        var read = set.Select(p =>
        {
            var currency = LoanViews.CurrencyOf(p.Item);
            var figures = FiguresOf(p, rates, today);
            return new Read(p, currency, figures,
                figures.Value is { } v ? convert.Of(v, currency, today) : null,
                figures.Value is not null ? convert.Of(figures.CostPrice, currency, today) : null);
        }).ToList();
        var owed = set.SelectMany(p => p.Loans).Select(l =>
        {
            var currency = LoanViews.CurrencyOf(l.Item);
            var terms = Terms(currency, l.Loan.Contract, l.Loan.Fees, l.Loan.Repayments);
            var schedule = Schedule(terms, l.Loan);
            var balance = LoanMath.At(terms, schedule, today).Balance;
            return new Owed(l.Item, currency, terms, schedule, balance, convert.Of(balance, currency, today));
        }).ToList();

        var valued = read.Where(r => r.Figures.Value is not null).ToList();
        var value = SumOrNull(valued.Select(r => r.Value));
        var balance = owed.Count == 0 ? 0m : SumOrNull(owed.Select(o => o.Converted));
        var net = value is { } v1 && balance is { } b1 ? v1 - b1 : (decimal?)null;
        var cost = SumOrNull(valued.Select(r => r.CostPrice));
        var gain = value is { } v2 && cost is { } c2 ? v2 - c2 : (decimal?)null;

        // D9: rents of the let properties; the yield on those whose yield is significant.
        var let = read.Where(r => r.Figures.Rental.RentedSince is not null).ToList();
        var rent = SumOrNull(let.Select(r => convert.Of(r.Figures.Rental.RentReceived, r.Currency, today)));
        var significant = read.Where(r => r.Figures.Rental.GrossYield is not null).ToList();
        var yieldRent = SumOrNull(significant.Select(r => convert.Of(r.Figures.Rental.RentReceived, r.Currency, today)));
        var yieldCost = SumOrNull(significant.Select(r => convert.Of(r.Figures.CostPrice, r.Currency, today)));

        var rows = read
            .OrderBy(r => r.Figures.Value is null ? 2 : r.Value is null ? 1 : 0).ThenByDescending(r => r.Value)
            .ThenBy(r => r.Source.Item.DisplayName, French).ThenBy(r => r.Source.Item.Id.Value)
            .Select(r => new PropertyOverviewRow(r.Source.Item.Id, r.Source.Item.DisplayName, LoanWire.Of(r.Source.Property.Details.Type), r.Currency.Code,
                r.Source.Item.OwnerViews(), Amount(r.Figures.Value), r.Figures.ValueOn, r.Figures.Stale, Amount(r.Figures.LinkedBalance),
                Amount(r.Figures.NetValue), Amount(r.Figures.LatentGain), Fraction(r.Figures.Rental.GrossYield), convert.View(r.Value),
                convert.View(r.Figures.NetValue is { } nv ? convert.Of(nv, r.Currency, today) : null)))
            .ToList();

        var byType = valued.GroupBy(r => LoanWire.Of(r.Source.Property.Details.Type))
            .Select(g => (Type: g.Key, Value: SumOrNull(g.Select(r => r.Value))))
            .OrderBy(t => t.Value is null).ThenByDescending(t => t.Value).ThenBy(t => t.Type, StringComparer.Ordinal)
            .Select(t => new PropertyTypeShareView(t.Type, convert.View(t.Value), Pct(t.Value, value)))
            .ToList();

        return new PropertiesOverview(reference.Code, read.Count, convert.View(value), valued.Select(r => r.Figures.ValueOn).Min(),
            valued.Count(r => r.Figures.Stale), read.Count - valued.Count,
            convert.View(balance), owed.Count(o => o.Balance > 0m), convert.View(net), Pct(net, value),
            convert.View(cost), convert.View(gain), Pct(gain, cost),
            let.Count == 0 ? null : convert.View(rent),
            yieldRent is { } yr && yieldCost is { } yc && yc > 0m ? Fraction(yr / yc) : null,
            byType, People(valued, owed, persons, net, convert), rows, Points(set, owed, grid, span, convert), Watch(read, today, irl), convert.Missing);
    }

    /// <summary>
    /// D2: a property's figures as its page's (<see cref="PropertyMath.At"/>), over its linked loans in the scope (D3); a private
    /// loan the scope left out is named, as the dashboard does, where the property's page would deduct it.
    /// </summary>
    internal static PropertyFigures FiguresOf(OverviewProperty p, RateTable rates, LocalDate today)
    {
        var currency = LoanViews.CurrencyOf(p.Item);
        var shared = p.Item.Visibility == Visibility.Shared;
        var figures = PropertyMath.At(p.Property.Details, shared, p.Values, p.Flows,
            p.Loans.Select(l => PropertyReading.Linked(l.Loan, l.Item, currency, rates, today)).ToList(),
            p.Property.Tenancies.Select(t => t.Tenancy).ToList(), today);
        return shared
            ? figures with { Warnings = [.. figures.Warnings, .. p.LeftOut.Select(id => new PropertyWarning(PropertyWarnings.VisibilityMismatch, id))] }
            : figures;
    }

    /// <summary>Rental receipts: a month's rent is watched once the month has been over for more than 10 days.</summary>
    private const int ArrearsGraceDays = 10;

    /// <summary>
    /// Spec §3.2: the detail's warnings (but a loan in another currency, which is no task), the rent ledger's arrears of the months
    /// over by more than <see cref="ArrearsGraceDays"/> days, and on 60 days, as « À venir », each planned cost's next due date and
    /// each tenancy's end; with the warnings, each chain's rent revision (rental spec §3.3). Warnings first, then by date.
    /// </summary>

    private static List<PropertyWatchView> Watch(IReadOnlyList<Read> read, LocalDate today, IReadOnlyList<IrlValue> irl)
    {
        var until = today.PlusDays(UpcomingDays);
        var warnings = new List<PropertyWatchView>();
        var dated = new List<PropertyWatchView>();
        foreach (var r in read)
        {
            var id = r.Source.Item.Id;
            foreach (var w in r.Figures.Warnings.Where(w => w.Code != PropertyWarnings.LoanOtherCurrency))
                warnings.Add(new PropertyWatchView(w.Code, id, w.LoanId, w.Code == PropertyWarnings.ValuationStale ? r.Figures.ValueOn : null, null));
            // The watch leaves the month just ended a grace of ArrearsGraceDays: the figure itself owes the current month from its 1st.
            if (r.Figures.Rental.RentedSince is not null
                && RentLedger.ArrearsDueBy(r.Source.Property.Tenancies.Select(t => t.Tenancy).ToList(), r.Source.Flows, today, ArrearsGraceDays) is > 0m and var owed)
                warnings.Add(new PropertyWatchView(PropertyOverviewCodes.RentArrears, id, null, null, Money(owed, r.Currency)));
            foreach (var chain in RentRevisions.Of(r.Source.Property, irl, today))
            {
                if (chain.Lost is { } lost)
                    warnings.Add(new PropertyWatchView(PropertyOverviewCodes.RevisionLost, id, null, lost, null));
                if (chain.State switch
                    {
                        RevisionDue d => new PropertyWatchView(PropertyOverviewCodes.RevisionDue, id, null, d.Anniversary, Money(d.NewRent, r.Currency)),
                        RevisionIrlMissing m => new PropertyWatchView(PropertyOverviewCodes.IrlMissing, id, null, m.Anniversary, null),
                        RevisionForbiddenDpe f => new PropertyWatchView(PropertyOverviewCodes.RevisionForbiddenDpe, id, null, f.Anniversary, null),
                        _ => null,
                    } is { } revision)
                    warnings.Add(revision);
            }
            foreach (var cost in r.Source.Property.Costs.Select(c => c.Cost))
                if (PropertyMath.NextDueOn(cost, today) is { } next && next <= until)
                    dated.Add(new PropertyWatchView(UpcomingKinds.PropertyCostDue, id, null, next, Money(cost.Amount, r.Currency)));
            foreach (var tenancy in RentRevisions.LastLinks(r.Source.Property).Select(t => t.Tenancy))
                if (tenancy.EndsOn is { } ends && ends >= today && ends <= until)
                    dated.Add(new PropertyWatchView(UpcomingKinds.TenancyEnd, id, null, ends, null));
        }
        return [.. warnings, .. dated.OrderBy(w => w.On).ThenBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.PropertyId.Value)];
    }

    /// <summary>
    /// D5: each person's part of each property's value and of each linked loan's balance, each by its own shares, as the dashboard
    /// splits the net worth; an item's last owner takes what is left. Adults first, then dependents, each in the household's order.
    /// </summary>
    private static List<PersonPropertyShareView> People(
        IReadOnlyList<Read> valued, IReadOnlyList<Owed> owed, IReadOnlyList<PersonInfo> persons, decimal? net, Converter convert)
    {
        var parts = new Dictionary<PersonId, (decimal? Value, decimal? Balance)>();
        void Add(ItemAccess item, decimal? amount, bool isLoan)
        {
            var owners = item.Owners;
            var split = Split(amount, owners);
            for (var k = 0; k < owners.Count; k++)
            {
                var (value, balance) = parts.GetValueOrDefault(owners[k].PersonId, (0m, 0m));
                parts[owners[k].PersonId] = isLoan ? (value, balance + split[k]) : (value + split[k], balance);
            }
        }
        foreach (var r in valued) Add(r.Source.Item, r.Value, false);
        foreach (var o in owed.Where(o => o.Balance > 0m)) Add(o.Item, o.Converted, true);

        var rank = persons.OrderBy(p => p.Kind == PersonKind.Adult ? 0 : 1).Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return parts.OrderBy(p => rank.GetValueOrDefault(p.Key, int.MaxValue)).ThenBy(p => p.Key.Value)
            .Select(p =>
            {
                var personNet = p.Value.Value is { } v && p.Value.Balance is { } b ? v - b : (decimal?)null;
                return new PersonPropertyShareView(p.Key.Value, convert.View(p.Value.Value), convert.View(p.Value.Balance), convert.View(personNet), Pct(personNet, net));
            })
            .ToList();
    }

    /// <summary>An amount by shares to the cent, the last owner taking the rest.</summary>
    private static List<decimal?> Split(decimal? amount, IReadOnlyList<Owner> owners)
    {
        if (amount is not { } total) return owners.Select(_ => (decimal?)null).ToList();
        var parts = new List<decimal?>();
        var given = 0m;
        for (var k = 0; k < owners.Count; k++)
        {
            var part = k == owners.Count - 1 ? total - given : Math.Round(total * owners[k].ShareBp / 10_000m, 2, MidpointRounding.AwayFromZero);
            given += part;
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>
    /// As the dashboard's curve (3E1 §2): each property's latest estimate on or before the date — before its first, from its
    /// purchase on, its purchase price; never estimated, nothing — each linked loan in the scope from its disbursement on,
    /// converted at the date. A date with neither has no point; « Tout » starts at the first date with one.
    /// </summary>
    private static List<PropertyPointView> Points(
        IReadOnlyList<OverviewProperty> set, IReadOnlyList<Owed> owed, IReadOnlyList<LocalDate> grid, PropertiesSpan span, Converter convert)
    {
        var points = new List<PropertyPointView>();
        foreach (var date in grid)
        {
            // SeriesManyAsync keeps SeriesAsync's as_of order.
            var estimates = set.Where(p => p.Values.Count > 0)
                .Select(p => (p, Value: p.Values.LastOrDefault(v => v.AsOf <= date)?.Value.Amount
                    ?? (date >= p.Property.Details.PurchaseDate ? p.Property.Details.PurchasePrice : (decimal?)null)))
                .Where(x => x.Value is not null)
                .Select(x => convert.Of(x.Value!.Value, LoanViews.CurrencyOf(x.p.Item), date)).ToList();
            var balances = owed.Where(o => date >= o.Terms.DisbursedOn)
                .Select(o => convert.Of(LoanMath.At(o.Terms, o.Schedule, date).Balance, o.Currency, date)).ToList();
            if (estimates.Count == 0 && balances.Count == 0)
            {
                points.Add(new PropertyPointView(date, null, null, null));
                continue;
            }
            var value = SumOrNull(estimates);
            var balance = balances.Count == 0 ? 0m : SumOrNull(balances);
            points.Add(new PropertyPointView(date, convert.View(value), convert.View(balance),
                convert.View(value is { } v && balance is { } b ? v - b : null)));
        }
        return span == PropertiesSpan.All ? points.SkipWhile(p => p.Value is null && p.LinkedBalance is null).ToList() : points;
    }

    private static LoanMoneyView Money(decimal amount, Currency currency) => new(Amount(amount), currency.Code);

    /// <summary>A part of a positive total in %, 0.1 %.</summary>
    private static decimal? Pct(decimal? part, decimal? total)
        => part is { } p && total is { } t && t > 0m ? Math.Round(p / t * 100m, 1, MidpointRounding.AwayFromZero) : null;

    private static string? Fraction(decimal? x) => x is { } f ? Math.Round(f, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture) : null;

    /// <summary>Null with nothing to sum (no figure is not a figure of 0), or when one amount is unknown.</summary>
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
