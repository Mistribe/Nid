using System.Globalization;
using Nid.Assets.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Assets.Application;

public sealed record MoneyView(string Amount, string Currency);

public sealed record ValuationView(Guid Id, LocalDate AsOf, string Amount, string? Note);

/// <param name="ValueFromPurchase">The value is the purchase price: no valuation is dated yet.</param>
public sealed record AssetView(
    ItemId Id, string Name, string Kind, MoneyView? Value, LocalDate? ValueOn, bool ValueFromPurchase, MoneyView? PurchasePrice,
    LocalDate? PurchasedOn, string? Note, decimal? WeightGrams, decimal? Fineness, IReadOnlyList<ValuationView> Valuations, string Currency,
    string Visibility, IReadOnlyList<ItemOwnerView> Owners, bool Archived, uint Version, bool CanWrite, string ShareLevel) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

public sealed record AssetRow(
    ItemId Id, string Name, string Kind, MoneyView? Value, LocalDate? ValueOn, bool ValueFromPurchase, string Currency, string Visibility,
    IReadOnlyList<ItemOwnerView> Owners, bool Archived);

/// <param name="Kinds">The kinds among the assets the actor reads, before the kind filter: what the filter offers.</param>
public sealed record AssetListView(IReadOnlyList<AssetRow> Rows, int Total, IReadOnlyList<string> Kinds) : IExposesItems
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
            value?.On, value?.FromPurchase ?? false, currency, Wire(item.Visibility), item.OwnerViews(), item.ArchivedAt is not null);
    }

    public static AssetView Detail(ItemAccess item, AssetRecord record, IReadOnlyList<Valuation> valuations, LocalDate today, bool canWrite)
    {
        var row = Row(item, record, valuations, today);
        var d = record.Details;
        return new AssetView(row.Id, row.Name, row.Kind, row.Value, row.ValueOn, row.ValueFromPurchase,
            d.PurchasePrice is { } price ? new MoneyView(Amount(price), row.Currency) : null, d.PurchasedOn, d.Note, d.WeightGrams, d.Fineness,
            valuations.OrderBy(v => v.AsOf).Select(v => new ValuationView(v.Id, v.AsOf, Amount(v.Value.Amount), v.Note)).ToList(),
            row.Currency, row.Visibility, row.Owners, row.Archived, record.Version, canWrite,
            item.Visibility == Visibility.Shared && item.ShareLevel == ShareLevel.Edit ? "edit" : "view");
    }
}
