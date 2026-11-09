using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Application;

/// <summary>
/// The rent of a lease chain (<c>tenancy:&lt;property&gt;:&lt;tenancy&gt;:rent</c>), one point per link from its start; whichever link names it,
/// the series is the whole chain's. It follows the property's access, not the selector.
/// </summary>
public sealed class TenancySeries(IVisibleItems visible, IItemRegistry items, IAccessPolicy policy, IAccessDenials denials, IPropertyStore properties) : ISeriesContribution
{
    public string Module => "realestate";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var found = await items.SearchAsync(ids, Module, "property", SeriesSearch.NameFilter(q, kinds, SeriesKinds.TenancyRent, "tenancy", "rent"), ct);
        var stored = await properties.FindManyAsync(found.Select(p => p.Id).ToList(), ct);
        var byId = found.ToDictionary(p => p.Id);
        return stored.Where(p => byId.ContainsKey(p.ItemId))
            .SelectMany(p => RentRevisions.LastLinks(p).Select(last => Info(byId[p.ItemId], last)))
            .ToList();
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key.Split(':') is not ["tenancy", var p, var t, "rent"] || !Guid.TryParse(p, out var propertyGuid) || !Guid.TryParse(t, out var tenancyId)) return null;
        var id = new ItemId(propertyGuid);
        if ((await policy.CheckAsync(reader, id, AccessKind.Read, ct)).TryGetFailure(out _))
        {
            await denials.RecordAsync(reader, id, "read", ct); // as the item-scoped pipeline does
            return null;
        }

        if (!(await PropertyCheck.LoadAsync(properties, items, id, ct)).TryGetValue(out var loaded)) return null;
        var (property, item) = loaded;
        // A tenancy of another property is not found here, whatever the property's access says.
        if (property.Tenancies.FirstOrDefault(s => s.Id == tenancyId) is not { } link) return null;
        var chain = RentRevisions.Chain(property, link);
        var points = chain.Select(l => new SeriesPoint(l.Tenancy.StartsOn, l.Tenancy.RentMonthly))
            .Where(c => (from is null || c.Date >= from) && (to is null || c.Date <= to)).OrderBy(c => c.Date).ToList();
        return new SeriesPoints(Info(item, chain[^1]), points);
    }

    private static SeriesInfo Info(ItemAccess property, StoredTenancy last)
        => new($"tenancy:{property.Id.Value}:{last.Id}:rent", SeriesKinds.TenancyRent, $"{property.DisplayName} · {last.Tenancy.Label}",
            $"currency:{property.Currency}", SeriesFrequencies.Irregular, null, null, property.Id);
}
