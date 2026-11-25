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
