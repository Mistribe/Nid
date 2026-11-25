using NodaTime;

namespace Nid.Assets.Infrastructure.Persistence;

public sealed class AssetRow
{
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public decimal? PurchasePrice { get; set; }
    public LocalDate? PurchasedOn { get; set; }
    public string? Note { get; set; }
    public decimal? WeightGrams { get; set; }
    public decimal? Fineness { get; set; }
    public LocalDate? RegisteredOn { get; set; }
    public int? MileageKm { get; set; }
    public LocalDate? MileageOn { get; set; }
    public short? FiscalHp { get; set; }
    public string? Energy { get; set; }
    public decimal? DepreciationRate { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}
