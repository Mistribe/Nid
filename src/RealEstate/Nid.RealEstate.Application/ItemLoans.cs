using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Application;

/// <summary>#188: the loans financing assets, through the port Assets reads: the balances as <see cref="PropertyReading"/> reads a property's loans.</summary>
public sealed class ItemLoans(ILoanStore loans, IItemRegistry items, IVisibleItems visible, IFxRates fx) : IItemLoans
{
    public Task<IReadOnlyDictionary<ItemId, IReadOnlyList<ItemLoanBalance>>> BalancesAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> targets, LocalDate on, CancellationToken ct)
        => EachAsync(reader, scope, targets, on, (loan, terms) => LoanMath.At(terms, LoanReading.Schedule(terms, loan), on).Balance,
            (id, name, money) => new ItemLoanBalance(id, name, money), ct);

    public Task<IReadOnlyDictionary<ItemId, IReadOnlyList<ItemLoanCost>>> CostsAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> targets, LocalDate from, LocalDate to, CancellationToken ct)
        => EachAsync(reader, scope, targets, to, (loan, terms) => LoanMath.CostBetween(terms, LoanReading.Schedule(terms, loan), from, to),
            (id, name, money) => new ItemLoanCost(id, name, money), ct);

    /// <summary>One figure per readable loan linked to the targets, in its own currency, converted to its target's at <paramref name="on"/>.</summary>
    private async Task<IReadOnlyDictionary<ItemId, IReadOnlyList<T>>> EachAsync<T>(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> targets, LocalDate on,
        Func<StoredLoan, LoanTerms, decimal> figure, Func<ItemId, string, Money?, T> make, CancellationToken ct)
    {
        var linked = await loans.FindLinkedToAssetsAsync(targets, ct);
        if (linked.Count == 0)
            return new Dictionary<ItemId, IReadOnlyList<T>>();

        var seen = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var readable = linked.Where(l => seen.Contains(l.ItemId)).ToList();
        var access = await items.FindManyAsync(readable.Select(l => l.ItemId).Concat(targets).ToList(), ct);
        var currencies = readable.Where(l => access.ContainsKey(l.ItemId) && access.ContainsKey(l.AssetId!.Value))
            .SelectMany(l => new[] { LoanReading.CurrencyOf(access[l.ItemId]), LoanReading.CurrencyOf(access[l.AssetId!.Value]) }).Distinct().ToList();
        var rates = currencies.Count == 0 ? RateTable.Empty(on, 0) : await fx.LoadAsync(currencies, on, on, ct);

        var result = new Dictionary<ItemId, List<T>>();
        foreach (var loan in readable.OrderBy(l => access.GetValueOrDefault(l.ItemId)?.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.ItemId.Value))
        {
            if (!access.TryGetValue(loan.ItemId, out var item) || !access.TryGetValue(loan.AssetId!.Value, out var target))
                continue;
            var currency = LoanReading.CurrencyOf(item);
            var amount = figure(loan, LoanReading.Terms(currency, loan.Contract, loan.Fees, loan.Repayments));
            Money? money = rates.TryConvert(amount, currency, LoanReading.CurrencyOf(target), on, projected: false, out var converted, out _)
                && Money.Create(decimal.Round(converted, Money.MaxDecimals), LoanReading.CurrencyOf(target)).TryGetValue(out var m)
                ? m : null;
            if (!result.TryGetValue(loan.AssetId.Value, out var list))
                result[loan.AssetId.Value] = list = [];
            list.Add(make(loan.ItemId, item.DisplayName, money));
        }
        return result.ToDictionary(e => e.Key, e => (IReadOnlyList<T>)e.Value);
    }
}
