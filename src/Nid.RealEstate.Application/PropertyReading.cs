using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>What a property's detail shows: its facts, the items of its readable linked loans (by name), and its figures.</summary>
public sealed record PropertyRead(IReadOnlyList<Valuation> Values, IReadOnlyList<Flow> Flows, IReadOnlyList<ItemAccess> Loans, PropertyFigures Figures);

/// <summary>
/// Spec 3C1 §5: a property's facts and figures today, read as the ambient actor, <c>reader</c>. Only the linked loans the
/// reader can read are counted, listed or warned about (spec §1.7): nothing reveals the others.
/// </summary>
public sealed class PropertyReading(
    IValuations valuations, IFlows flows, ILoanStore loans, IItemRegistry items, IAccessPolicy policy, IFxRates fx)
{
    /// <summary>Every current fact, whenever dated.</summary>
    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

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
        readable = readable.OrderBy(l => l.Item.DisplayName, French).ThenBy(l => l.Item.Id.Value).ToList();

        var currency = LoanViews.CurrencyOf(item);
        var foreign = readable.Select(l => LoanViews.CurrencyOf(l.Item)).Where(c => c != currency).Distinct().ToList();
        // Rates are per USD: the property's own quote is needed too (as the dashboard's reference).
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, currency], today, today, ct);
        var linked = readable.Select(l =>
        {
            var loanCurrency = LoanViews.CurrencyOf(l.Item);
            var terms = LoanReading.Terms(loanCurrency, l.Loan.Contract, l.Loan.Fees, l.Loan.Repayments);
            decimal? rate = rates.TryConvert(1m, loanCurrency, currency, today, projected: false, out var converted, out _) ? converted : null;
            return new LinkedLoan(l.Item.Id, loanCurrency != currency, l.Item.Visibility == Visibility.Private,
                LoanMath.At(terms, LoanReading.Schedule(terms), today), rate);
        }).ToList();

        return Result.Success(new PropertyRead(values!, flowList!, readable.Select(l => l.Item).ToList(),
            PropertyMath.At(property.Details, item.Visibility == Visibility.Shared, values!, flowList!, linked,
                property.Tenancies.Select(t => t.Tenancy).ToList(), today)));
    }
}
