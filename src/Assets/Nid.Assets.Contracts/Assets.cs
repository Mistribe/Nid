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
