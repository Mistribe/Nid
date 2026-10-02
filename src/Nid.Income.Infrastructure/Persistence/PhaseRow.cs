using NodaTime;

namespace Nid.Income.Infrastructure.Persistence;

public sealed class PhaseRow
{
    public Guid Id { get; set; }
    public Guid SourceItemId { get; set; }
    public string Label { get; set; } = "";
    public LocalDate StartsOn { get; set; }
    public LocalDate? EndsOn { get; set; }
    public decimal NetAmount { get; set; }
    public decimal? GrossAmount { get; set; }
    public string Currency { get; set; } = "";
    public int EveryCount { get; set; }
    public string EveryUnit { get; set; } = "";
    public int? MaxCycles { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}
