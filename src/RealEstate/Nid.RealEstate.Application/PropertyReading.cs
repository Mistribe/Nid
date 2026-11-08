using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>What a property's detail shows: its facts, the items of its readable linked loans (by name), and its figures.</summary>
public sealed record PropertyRead(
    IReadOnlyList<Valuation> Values, IReadOnlyList<Flow> Flows, IReadOnlyList<ItemAccess> Loans, PropertyFigures Figures, IReadOnlyList<LoanSource> Sources);

/// <summary>A readable linked loan, its schedule and its rate to the property's currency (null when missing).</summary>
public sealed record LoanSource(ItemAccess Item, LoanTerms Terms, LoanSchedule Schedule, bool OtherCurrency, decimal? Rate);

/// <summary>
/// Spec 3C1 §5: a property's facts and figures today, read as the ambient actor, <c>reader</c>. Only the linked loans the
/// reader can read are counted, listed or warned about (spec §1.7): nothing reveals the others.
/// </summary>
public sealed class PropertyReading(
    IValuations valuations, IFlows flows, ILoanStore loans, IItemRegistry items, IAccessPolicy policy, IFxRates fx)
{
    /// <summary>Every current fact, whenever dated.</summary>
    internal static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async Task<Result<PropertyRead>> ReadAsync(StoredProperty property, ItemAccess item, PersonId reader, LocalDate today, CancellationToken ct)
    {
        var series = await valuations.SeriesAsync(property.ItemId, Everything, ct);
        if (series.TryGetFailure(out var failure))
            return Result.Failure<PropertyRead>(failure!);
        var listed = await flows.ListAsync(property.ItemId, Everything, null, ct);
        if (listed.TryGetFailure(out failure))
            return Result.Failure<PropertyRead>(failure!);
        series.TryGetValue(out var values);
        listed.TryGetValue(out var flowList);

        var readable = new List<(StoredLoan Loan, ItemAccess Item)>();
        foreach (var loan in await loans.FindLinkedAsync([property.ItemId], ct))
            if ((await policy.CheckAsync(reader, loan.ItemId, AccessKind.Read, ct)).IsSuccess && await items.FindAsync(loan.ItemId, ct) is { } loanItem)
                readable.Add((loan, loanItem));

        var currency = LoanViews.CurrencyOf(item);
        var foreign = Foreign(readable, currency);
        // Rates are per USD: the property's own quote is needed too (as the dashboard's reference).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, currency], today, today, ct);
        return Result.Success(Compose(property, item, values!, flowList!, readable, rates, today));
    }

    /// <summary>
    /// Batched reads spec §3: <see cref="ReadAsync"/> for the list, in a fixed number of queries whatever the number of
    /// properties — their facts as one block (refused as a whole when one cannot be read), their linked loans the reader can
    /// read, and the rates once. Every property passed must be known to the registry.
    /// </summary>
    public async Task<Result<IReadOnlyDictionary<ItemId, PropertyRead>>> ReadManyAsync(
        IReadOnlyList<(StoredProperty Property, ItemAccess Item)> properties, PersonId reader, LocalDate today, CancellationToken ct)
    {
        var ids = properties.Select(p => p.Property.ItemId).ToList();
        var series = await valuations.SeriesManyAsync(ids, Everything, ct);
        if (series.TryGetFailure(out var failure))
            return Result.Failure<IReadOnlyDictionary<ItemId, PropertyRead>>(failure!);
        var listed = await flows.ListManyAsync(ids, Everything, null, ct);
        if (listed.TryGetFailure(out failure))
            return Result.Failure<IReadOnlyDictionary<ItemId, PropertyRead>>(failure!);
        series.TryGetValue(out var values);
        listed.TryGetValue(out var flowLists);

        var linked = await loans.FindLinkedAsync(ids, ct);
        var readable = await policy.CheckManyAsync(reader, linked.Select(l => l.ItemId).ToList(), AccessKind.Read, ct);
        var loanItems = await items.FindManyAsync(readable.ToList(), ct);
        var byProperty = linked
            .Where(l => loanItems.ContainsKey(l.ItemId))
            .ToLookup(l => l.PropertyId!.Value, l => (Loan: l, Item: loanItems[l.ItemId]));

        // One load for the list: the quotes of every property with a loan in another currency, with that property's own.
        var quotes = new HashSet<Currency>();
        foreach (var (property, item) in properties)
        {
            var currency = LoanViews.CurrencyOf(item);
            var foreign = Foreign(byProperty[property.ItemId], currency);
            if (foreign.Count > 0)
                quotes.UnionWith([.. foreign, currency]);
        }
        var rates = quotes.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(quotes, today, today, ct);

        return Result.Success<IReadOnlyDictionary<ItemId, PropertyRead>>(properties.ToDictionary(
            p => p.Property.ItemId,
            p => Compose(p.Property, p.Item, values![p.Property.ItemId], flowLists![p.Property.ItemId], byProperty[p.Property.ItemId], rates, today)));
    }

    /// <summary>The loans' currencies other than the property's: what its conversions need, besides its own quote.</summary>
    private static IReadOnlyList<Currency> Foreign(IEnumerable<(StoredLoan Loan, ItemAccess Item)> loans, Currency currency)
        => loans.Select(l => LoanViews.CurrencyOf(l.Item)).Where(c => c != currency).Distinct().ToList();

    /// <summary>One property's read from its facts, its readable linked loans and the rates: the detail's and the list's.</summary>
    private static PropertyRead Compose(
        StoredProperty property, ItemAccess item, IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flowList,
        IEnumerable<(StoredLoan Loan, ItemAccess Item)> loans, RateTable rates, LocalDate today)
    {
        var currency = LoanViews.CurrencyOf(item);
        var readable = loans.OrderBy(l => l.Item.DisplayName, French).ThenBy(l => l.Item.Id.Value).ToList();
        var sources = readable.Select(l => Source(l.Loan, l.Item, currency, rates, today)).ToList();
        var linked = sources.Select(s => Linked(s, today)).ToList();

        return new PropertyRead(values, flowList, readable.Select(l => l.Item).ToList(),
            PropertyMath.At(property.Details, item.Visibility == Visibility.Shared, values, flowList, linked,
                property.Tenancies.Select(t => (t.Id, t.RevisedFrom, t.Tenancy)).ToList(), today), sources);
    }

    /// <summary>A linked loan at <paramref name="today"/>, with its rate to the property's currency: the detail's and the overview's.</summary>
    internal static LinkedLoan Linked(StoredLoan loan, ItemAccess item, Currency currency, RateTable rates, LocalDate today)
        => Linked(Source(loan, item, currency, rates, today), today);

    private static LinkedLoan Linked(LoanSource s, LocalDate today)
        => new(s.Item.Id, s.OtherCurrency, s.Item.Visibility == Visibility.Private, LoanMath.At(s.Terms, s.Schedule, today), s.Rate);

    private static LoanSource Source(StoredLoan loan, ItemAccess item, Currency currency, RateTable rates, LocalDate today)
    {
        var loanCurrency = LoanViews.CurrencyOf(item);
        var terms = LoanReading.Terms(loanCurrency, loan.Contract, loan.Fees, loan.Repayments);
        decimal? rate = rates.TryConvert(1m, loanCurrency, currency, today, projected: false, out var converted, out _) ? converted : null;
        return new LoanSource(item, terms, LoanReading.Schedule(terms, loan), loanCurrency != currency, rate);
    }
}
