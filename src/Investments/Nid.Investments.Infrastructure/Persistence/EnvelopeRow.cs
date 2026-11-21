using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class EnvelopeRow
{
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string? Institution { get; set; }
    public string? AccountHint { get; set; }
    public LocalDate? OpenedOn { get; set; }
    public string ValuationCadence { get; set; } = "monthly";
    public int? StaleAfterDays { get; set; }
    public string? Note { get; set; }
    public LocalDate? ClosedOn { get; set; }
    public decimal? UcShareFloorPct { get; set; }
    public decimal? PremiumsBefore2017 { get; set; }
    public string? Regulated { get; set; }
    /// <summary>#184: the tranches of an employee savings plan, a JSON array of <c>{ "availableOn": "yyyy-mm-dd", "amount": "…" }</c>.</summary>
    public string? Availability { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}
