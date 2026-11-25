namespace Nid.Assets.Contracts;

public static class AssetsCodes
{
    public const string UnknownKind = "assets.unknown_kind";
    public const string ValueRequired = "assets.value_required";
    public const string PurchaseIncomplete = "assets.purchase_incomplete";
    public const string MetalOnly = "assets.metal_only";
    public const string InvalidFineness = "assets.invalid_fineness";
    public const string InvalidWeight = "assets.invalid_weight";
    public const string NotFound = "assets.not_found";
    public const string InvalidPurchase = "assets.invalid_purchase";
    public const string InvalidValue = "assets.invalid_value";
    public const string Conflict = "assets.conflict";
    /// <summary>A flow of an asset: its only facts are valuations.</summary>
    public const string NoFlows = "assets.no_flows";
}
