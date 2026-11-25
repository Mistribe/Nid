using Nid.Assets.Domain;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Assets.Application;

/// <summary>
/// Issue #186: each visible asset at its latest valuation (its purchase price while none is dated), one <see cref="Holding"/> each, always
/// <c>other</c> and <c>locked</c>. A vehicle with a depreciation rule is an <b>estimate</b> (#187): the base worn down to today, its date the base's. Since 1 January the valuation change is a gain; an asset first counted this year enters as a purchase
/// (<c>Moved</c>), never as a gain. Batched reads: the items and the valuations are read once for all, whatever their number.
/// </summary>
public sealed class AssetsNetWorth(IVisibleItems visible, IItemRegistry items, IAssetRecordStore assets, IValuations valuations, IHouseholdCalendar calendar)
    : INetWorthContribution
{
    public string Module => "assets";

    public async Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(await visible.ForAsync(reader, scope, ct), ct);
        var january1 = new LocalDate(today.Year, 1, 1);
        var values = new Dictionary<Currency, decimal>();
        var starts = new Dictionary<Currency, decimal>();
        var nets = new Dictionary<Currency, decimal>();
        var gains = new Dictionary<Currency, decimal>();
        var holdings = new List<Holding>();
        var count = 0;
        var withoutValue = 0;
        LocalDate? oldest = null;

        foreach (var (record, item, currency, series) in read)
        {
            count++;
            var owners = Owners(item);
            if (currency is not { } cur || series is null || AssetMath.At(record.Details, series, today) is not { } now)
            {
                withoutValue++; // an unreadable currency or valuations count as no value, never skipped: no total is silently partial
                holdings.Add(new Holding(record.ItemId, "assets", item.DisplayName, null, null, false, owners));
                continue;
            }
            Add(values, cur, now.Amount);
            if (!now.FromPurchase && (oldest is null || now.On < oldest))
                oldest = now.On; // « values as of » follows valuations, not purchase dates
            holdings.Add(new Holding(
                record.ItemId, "assets", item.DisplayName, MoneyOf(now.Amount, cur), now.On, false, owners,
                [new AllocationSlice(AllocationClasses.Other, AllocationLiquidity.Locked, now.Amount, false)],
                Estimated: now.Estimated,
                ValuedOn: now.FromPurchase && !now.Estimated ? null : now.On)); // a bare purchase price is no estimate; a depreciated one is, dated by its base

            // Worth on 1 January, else (counted since) what it entered at: a purchase is no gain.
            var start = AssetMath.At(record.Details, series, january1)?.Amount ?? AssetMath.FirstCounted(record.Details, series)?.Amount ?? now.Amount;
            Add(starts, cur, start);
            Add(nets, cur, 0m);
            Add(gains, cur, now.Amount - start);
        }

        return Result.Success(new NetWorthPart(count, withoutValue, 0, oldest, values, starts, nets, gains, holdings, 0));
    }

    /// <summary>Nothing to save into: an asset is bought, not paid into.</summary>
    public Task<Result<IReadOnlyDictionary<Currency, decimal>>> NetContributionsAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
        => Task.FromResult(Result.Success<IReadOnlyDictionary<Currency, decimal>>(new Dictionary<Currency, decimal>()));

    /// <summary>
    /// At each date, each asset's latest valuation on or before it (its purchase price from its date until the first one). <c>Moved</c> holds
    /// the purchases and sales: the price from the first day it is counted, − what it was last counted at from its archive day on. An
    /// archived (sold) asset counts strictly before its archive day.
    /// </summary>
    public async Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        var read = await ReadAsync(await visible.ForAsync(reader, scope, includeArchived: true, ct), ct);
        var zone = await calendar.ZoneAsync(ct);
        var points = new List<NetWorthPoint>();
        foreach (var date in dates)
        {
            var values = new Dictionary<Currency, decimal>();
            var moved = new Dictionary<Currency, decimal>();
            var count = 0;
            foreach (var (record, item, cur, series) in read)
            {
                if (cur is not { } currency || series is null)
                    continue; // no value to count
                var archived = item.ArchivedAt?.InZone(zone).Date;
                if (AssetMath.FirstCounted(record.Details, series) is { } first && first.On <= date && (archived is not { } a || first.On < a))
                    Add(moved, currency, first.Amount);
                if (archived is { } sold && date >= sold)
                {
                    if (AssetMath.At(record.Details, series, sold.PlusDays(-1)) is { } last)
                        Add(moved, currency, -last.Amount);
                }
                else if (AssetMath.At(record.Details, series, date) is { } value)
                {
                    count++;
                    Add(values, currency, value.Amount);
                }
            }
            points.Add(new NetWorthPoint(date, values, new Dictionary<Currency, decimal>(), count, moved));
        }
        return Result.Success<IReadOnlyList<NetWorthPoint>>(points);
    }

    /// <summary>Currency or series is null when it could not be read: the asset then has no value.</summary>
    private async Task<List<(AssetRecord Record, ItemAccess Item, Currency? Currency, IReadOnlyList<(LocalDate On, decimal Amount)>? Series)>> ReadAsync(
        IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var found = await assets.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync(found.Select(a => a.ItemId).ToList(), ct);
        // Valuations that cannot be read leave the assets without value, as a property's estimates: never the purchase price.
        var series = (await valuations.SeriesManyAsync(found.Select(a => a.ItemId).Where(accesses.ContainsKey).ToList(), AssetCheck.Everything, ct))
            .TryGetValue(out var map) ? map : null;
        var read = new List<(AssetRecord, ItemAccess, Currency?, IReadOnlyList<(LocalDate, decimal)>?)>();
        foreach (var record in found)
            if (accesses.TryGetValue(record.ItemId, out var item))
                read.Add((record, item, Currency.TryCreate(item.Currency ?? "", out var currency) ? currency : null,
                    series is null ? null : (series.GetValueOrDefault(record.ItemId) ?? []).Select(v => (v.AsOf, v.Value.Amount)).ToList()));
        return read;
    }

    private static void Add(Dictionary<Currency, decimal> sums, Currency currency, decimal amount) => sums[currency] = sums.GetValueOrDefault(currency) + amount;

    private static IReadOnlyList<OwnerShare> Owners(ItemAccess item) => item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();

    private static Money MoneyOf(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var money) ? money : throw new InvalidOperationException("A checked amount has two decimals.");
}
