using Nid.Core.Contracts;

namespace Nid.Assets.Contracts;

public static class AssetKinds
{
    public const string Vehicle = "vehicle";
    public const string PreciousMetal = "precious_metal";
    public const string Art = "art";
    public const string Collectible = "collectible";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Vehicle, PreciousMetal, Art, Collectible, Other];
}

/// <summary>The energies of a vehicle (wire values).</summary>
public static class AssetEnergies
{
    public const string Petrol = "petrol";
    public const string Diesel = "diesel";
    public const string Hybrid = "hybrid";
    public const string PlugInHybrid = "plug_in_hybrid";
    public const string Electric = "electric";
    public const string Lpg = "lpg";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Petrol, Diesel, Hybrid, PlugInHybrid, Electric, Lpg, Other];
}

/// <summary>#649: the categories of a vehicle's running cost (wire values); a cost is an expense.</summary>
public static class AssetCostCategories
{
    public const string Service = "service";
    public const string Repair = "repair";
    public const string Tyres = "tyres";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Service, Repair, Tyres, Other];
}

/// <summary>A vehicle a person may record a cost on.</summary>
public sealed record VehicleChoice(ItemId Id, string Name, string Currency);

/// <summary>#649: the vehicles a person writes, for the bank reconciliation to offer when a line pays a running cost.</summary>
public interface IWritableVehicles
{
    Task<IReadOnlyList<VehicleChoice>> ForAsync(PersonId person, CancellationToken ct);

    /// <summary>Whether <paramref name="item"/> is a vehicle the person writes (one asset looked up, not the list).</summary>
    Task<bool> IsWritableVehicleAsync(PersonId person, ItemId item, CancellationToken ct);
}
