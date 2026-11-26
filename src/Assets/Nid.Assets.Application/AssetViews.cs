using System.Globalization;
using Nid.Assets.Domain;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;

namespace Nid.Assets.Application;

public sealed record MoneyView(string Amount, string Currency);

public sealed record ValuationView(Guid Id, LocalDate AsOf, string Amount, string? Note);

/// <summary>#648: a subscription linked to the asset, as its page lists it; <paramref name="Every"/> is its billing period.</summary>
public sealed record EveryView(int Count, string Unit);

public sealed record LinkedSubscriptionView(ItemId Id, string Name, MoneyView? Price, EveryView? Every, LocalDate? NextDeadline);

/// <summary>#188: a readable loan financing the asset, with the capital still owed on it, in the asset's currency; null when no exchange rate converts it (it is then not deducted).</summary>
public sealed record LinkedLoanView(ItemId Id, string Name, MoneyView? Balance);

/// <param name="Loans">#188: the readable loans financing it; <paramref name="NetValue"/> is the value less their balances, null without a loan or without a value.</param>
/// <param name="HasCosts">An asset that is not a vehicle still holds running costs (it was one, or a file named them): its costs stay readable and removable. Always false for a vehicle, whose tab is there anyway.</param>
/// <param name="ValueFromPurchase">The value is the purchase price: no valuation is dated yet.</param>
/// <param name="ValueEstimated">The value is worn down by the depreciation rule from <paramref name="ValueBase"/>, dated <paramref name="ValueOn"/> (a valuation, or the purchase when <paramref name="ValueFromPurchase"/>).</param>
public sealed record AssetView(
    ItemId Id, string Name, string Kind, MoneyView? Value, LocalDate? ValueOn, bool ValueFromPurchase, MoneyView? PurchasePrice,
    LocalDate? PurchasedOn, string? Note, decimal? WeightGrams, decimal? Fineness,
    LocalDate? RegisteredOn, int? MileageKm, LocalDate? MileageOn, int? FiscalHp, string? Energy, IReadOnlyList<ValuationView> Valuations, string Currency,
    string Visibility, IReadOnlyList<ItemOwnerView> Owners, bool Archived, uint Version, bool CanWrite, string ShareLevel,
    IReadOnlyList<LinkedSubscriptionView> Subscriptions, bool HasCosts = false, decimal? DepreciationRate = null,
    bool ValueEstimated = false, MoneyView? ValueBase = null, IReadOnlyList<LinkedLoanView>? Loans = null, MoneyView? NetValue = null, bool FxMissing = false) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id, ..Subscriptions.Select(s => s.Id), ..(Loans ?? []).Select(l => l.Id)];
}

public sealed record AssetRow(
    ItemId Id, string Name, string Kind, MoneyView? Value, LocalDate? ValueOn, bool ValueFromPurchase, string Currency, string Visibility,
    IReadOnlyList<ItemOwnerView> Owners, bool Archived, bool ValueEstimated = false, MoneyView? ValueBase = null);

/// <summary>
/// The listed assets of the same owners and shares, as the income list gathers a person's salaries. <paramref name="Value"/> adds
/// up their values in the household's currency, converted today; null when a rate is missing or none has a value.
/// </summary>
public sealed record AssetGroupView(IReadOnlyList<ItemOwnerView> Owners, IReadOnlyList<Guid> Items, MoneyView? Value);

/// <param name="Kinds">The kinds among the assets the actor reads, before the kind filter: what the filter offers.</param>
/// <param name="Groups">The page's rows by owners and shares, in the rows' order.</param>
public sealed record AssetListView(IReadOnlyList<AssetRow> Rows, int Total, IReadOnlyList<string> Kinds, IReadOnlyList<AssetGroupView> Groups) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Rows.Select(r => r.Id);
}

internal static class AssetViews
{
    private static string Amount(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Wire(Visibility v) => v == Visibility.Shared ? "shared" : "private";

    private static IReadOnlyList<(LocalDate On, decimal Amount)> Points(IReadOnlyList<Valuation> valuations)
        => valuations.Select(v => (v.AsOf, v.Value.Amount)).ToList();

    public static AssetRow Row(ItemAccess item, AssetRecord record, IReadOnlyList<Valuation> valuations, LocalDate today)
    {
        var value = AssetMath.At(record.Details, Points(valuations), today);
        var currency = item.Currency ?? "";
        return new AssetRow(record.ItemId, item.DisplayName, record.Details.Kind, value is null ? null : new MoneyView(Amount(value.Amount), currency),
            value?.On, value?.FromPurchase ?? false, currency, Wire(item.Visibility), item.OwnerViews(), item.ArchivedAt is not null,
            value?.Estimated ?? false, value?.BaseAmount is { } b ? new MoneyView(Amount(b), currency) : null);
    }

    public static AssetView Detail(ItemAccess item, AssetRecord record, IReadOnlyList<Valuation> valuations, LocalDate today, bool canWrite, IReadOnlyList<LinkedSubscription> subscriptions, bool hasCosts, IReadOnlyList<ItemLoanBalance> loans)
    {
        var row = Row(item, record, valuations, today);
        var d = record.Details;
        // #188: display only (the net worth already counts every loan as a holding of its own).
        var net = AssetMath.At(d, Points(valuations), today) is { } value && loans.Count > 0
            ? new MoneyView(Amount(value.Amount - loans.Sum(l => l.Balance?.Amount ?? 0m)), row.Currency)
            : null;
        return new AssetView(row.Id, row.Name, row.Kind, row.Value, row.ValueOn, row.ValueFromPurchase,
            d.PurchasePrice is { } price ? new MoneyView(Amount(price), row.Currency) : null, d.PurchasedOn, d.Note, d.WeightGrams, d.Fineness,
            d.RegisteredOn, d.MileageKm, d.MileageOn, d.FiscalHp, d.Energy,
            valuations.OrderBy(v => v.AsOf).Select(v => new ValuationView(v.Id, v.AsOf, Amount(v.Value.Amount), v.Note)).ToList(),
            row.Currency, row.Visibility, row.Owners, row.Archived, record.Version, canWrite,
            item.Visibility == Visibility.Shared && item.ShareLevel == ShareLevel.Edit ? "edit" : "view",
            subscriptions.Select(s => new LinkedSubscriptionView(
                s.Id, s.Name, s.Price is { } p ? new MoneyView(p.AmountText, p.Currency.Code) : null, s.Every is { } e ? new EveryView(e.Count, e.Unit.ToString().ToLowerInvariant()) : null, s.NextDeadline)).ToList(), hasCosts, d.DepreciationRate, row.ValueEstimated, row.ValueBase,
            loans.Select(l => new LinkedLoanView(l.Loan, l.Name, l.Balance is { } b ? new MoneyView(Amount(b.Amount), b.Currency.Code) : null)).ToList(), net,
            loans.Any(l => l.Balance is null));
    }
}
