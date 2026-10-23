using NodaTime;

namespace Nid.Goals.Infrastructure.Persistence;

public sealed class GoalRow
{
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public decimal? TargetAmount { get; set; }
    public int? Months { get; set; }
    public LocalDate? TargetDate { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

public sealed class FundingRow
{
    public Guid GoalId { get; set; }
    public Guid EnvelopeId { get; set; }
    public int ShareBp { get; set; }
}

public sealed class ProgressEntryRow
{
    public Guid GoalId { get; set; }
    public LocalDate On { get; set; }
    public decimal Amount { get; set; }
}
