using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Sale tax spec §3 and §6: the visible active properties, of every type — their purchase and its fees, the works dated until today
/// and the latest valuation. Facts that cannot be read leave every property without a value, as on the dashboard.
/// </summary>
public sealed class PropertiesSale(IVisibleItems visible, IItemRegistry items, IPropertyStore properties, IValuations valuations, IFlows flows)
    : ISaleContribution
{
    public async Task<Result<SaleAssets>> SaleAssetsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var found = await properties.FindManyAsync(await visible.ForAsync(reader, scope, includeArchived: false, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var estimates = await DashboardParts.EstimatesAsync(valuations, found, accesses, ct);
        var expenses = (await flows.ListManyAsync(found.Select(p => p.ItemId).Where(accesses.ContainsKey).ToList(), PropertyReading.Everything,
            new HashSet<FlowKind> { FlowKind.Expense }, ct)).TryGetValue(out var read) ? read : null;

        var list = new List<PropertyForSale>();
        foreach (var property in found)
        {
            if (!accesses.TryGetValue(property.ItemId, out var item))
                continue;
            var d = property.Details;
            var spent = expenses?.GetValueOrDefault(property.ItemId);
            // A value without the works it bears would overstate the gain: one unread, no value.
            var now = estimates?.GetValueOrDefault(property.ItemId) is { } values && spent is not null
                ? PropertyMath.At(d, false, values, [], [], [], today)
                : null;
            var works = spent?.Where(f => f.Category == PropertyCategories.Works && f.OccurredOn <= today).Sum(f => f.Amount.Amount) ?? 0m;
            list.Add(new PropertyForSale(item.Id, item.DisplayName, item.Currency, item.Owners, LoanWire.Of(d.Type), d.PurchaseDate,
                d.PurchasePrice, d.NotaryFees + d.AgencyFees + d.OtherAcquisitionCosts, works, now?.Value, now?.ValueOn, now?.Stale ?? false));
        }
        return Result.Success(new SaleAssets(list, []));
    }
}
