using NodaTime;

namespace Nid.Income.Infrastructure.Persistence;

public sealed class SourceRow
{
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string? PayerLabel { get; set; }
    public string? Note { get; set; }
    public LocalDate? EndedOn { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}
