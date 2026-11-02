using NodaTime;

namespace Nid.Childcare.Infrastructure.Persistence;

public sealed class ArrangementRow
{
    public Guid ItemId { get; set; }
    public Guid ChildId { get; set; }
    public string CarerName { get; set; } = "";
    public string? CarerPajemploi { get; set; }
    public string? CarerApproval { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

public sealed class TermsRow
{
    public Guid ItemId { get; set; }
    public LocalDate From { get; set; }
    public LocalDate? To { get; set; }
    public decimal NetHourlyRate { get; set; }
    public decimal? GrossHourlyRate { get; set; }
    public decimal OvertimeRate { get; set; }
    public decimal UpkeepPer9h { get; set; }
    public decimal? MealPrice { get; set; }
    public int ProgrammedWeeks { get; set; }
}

public sealed class WeekdayRow
{
    public Guid ItemId { get; set; }
    public LocalDate TermsFrom { get; set; }
    public short Day { get; set; }
    public LocalTime Arrival { get; set; }
    public LocalTime Departure { get; set; }
    public bool Meal { get; set; }
}

public sealed class GapRow
{
    public Guid ItemId { get; set; }
    public LocalDate Monday { get; set; }
    public int Weeks { get; set; }
}
