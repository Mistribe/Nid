using Nid.Assets.Contracts;
using Nid.Core.Contracts;

namespace Nid.Assets.Application;

/// <summary>The vehicles a person reads (archived ones left out) and writes, by name, for the bank reconciliation (#649).</summary>
public sealed class WritableVehicles(IVisibleItems visible, IItemRegistry items, IAssetRecordStore assets, IAccessPolicy policy) : IWritableVehicles
{
    public async Task<IReadOnlyList<VehicleChoice>> ForAsync(PersonId person, CancellationToken ct)
    {
        var seen = await visible.ForAsync(person, VisibilityScope.Me, ct);
        var candidates = (await items.SearchAsync(seen.ToList(), "assets", "asset", null, ct)).ToDictionary(i => i.Id);
        if (candidates.Count == 0)
            return [];
        var vehicles = (await assets.ListAsync(candidates.Keys.ToList(), AssetKinds.Vehicle, 0, int.MaxValue, ct)).Rows.Select(r => r.ItemId).ToList();
        var writable = await policy.CheckManyAsync(person, vehicles, AccessKind.Write, ct);
        return [.. vehicles.Where(writable.Contains).Select(v => candidates[v]).OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new VehicleChoice(i.Id, i.DisplayName, i.Currency ?? ""))];
    }
}
