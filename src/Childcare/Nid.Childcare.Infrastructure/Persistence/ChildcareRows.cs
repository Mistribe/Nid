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
    public decimal? LunchPrice { get; set; }
    public decimal? SnackPrice { get; set; }
    public string LunchProvider { get; set; } = "nanny";
    public string SnackProvider { get; set; } = "nanny";
    public string[] UnpaidAbsences { get; set; } = [];
    public bool FullYear { get; set; }
}

public sealed class WeekTypeRow
{
    public Guid ItemId { get; set; }
    public LocalDate TermsFrom { get; set; }
    public short Rank { get; set; }
    public string Name { get; set; } = "";
    public int Weeks { get; set; }
}

public sealed class WeekdayRow
{
    public Guid ItemId { get; set; }
    public LocalDate TermsFrom { get; set; }
    public short WeekType { get; set; }
    public short Day { get; set; }
    public LocalTime Arrival { get; set; }
    public LocalTime Departure { get; set; }
    public bool Lunch { get; set; }
    public bool Snack { get; set; }
}

public sealed class WeekOverrideRow
{
    public Guid ItemId { get; set; }
    public LocalDate Monday { get; set; }
    public short WeekType { get; set; }
}

public sealed class GapRow
{
    public Guid ItemId { get; set; }
    public LocalDate Monday { get; set; }
    public int Weeks { get; set; }
}

public sealed class StatementRow
{
    public Guid ItemId { get; set; }
    /// <summary>The month's first day.</summary>
    public LocalDate Month { get; set; }
    public bool Validated { get; set; }
    public decimal Mileage { get; set; }
    public decimal? Cmg { get; set; }
    /// <summary>#30: the contributions the Pajemploi summary leaves to the household; null while not entered.</summary>
    public decimal? Contributions { get; set; }
    /// <summary>The contributions Pajemploi waived on the extra hours: entered on a draft (null while not), frozen at validation.</summary>
    public decimal? Exemption { get; set; }
    public decimal? DeclaredNetPay { get; set; }
    public decimal? DeclaredUpkeep { get; set; }
    public decimal? DeclaredMeals { get; set; }
    public int? DeclaredNormalHours { get; set; }
    public int? DeclaredActivityDays { get; set; }
    public int? DeclaredPaidLeaveDays { get; set; }
    public decimal? DeclaredComplementaryHours { get; set; }
    public decimal? DeclaredOvertimeHours { get; set; }
    public Instant? ValidatedAt { get; set; }
    public int? NormalHours { get; set; }
    public int? ActivityDays { get; set; }
    public int? PaidLeaveDays { get; set; }
    public decimal? ComplementaryHours { get; set; }
    public decimal? OvertimeHours { get; set; }
    public decimal? NetPay { get; set; }
    public decimal? Upkeep { get; set; }
    public decimal? Meals { get; set; }
    public decimal? BasePay { get; set; }
    public decimal? Deduction { get; set; }
    public decimal? ExtraPay { get; set; }
    public decimal? PaidLeavePay { get; set; }
    public decimal? Cost { get; set; }
    public decimal? TaxBase { get; set; }
    public int? ChildSickDeducted { get; set; }
    public int? PaidLeaveTaken { get; set; }
    public uint Version { get; set; }
}

public sealed class StatementDayRow
{
    public Guid ItemId { get; set; }
    public LocalDate Month { get; set; }
    public LocalDate Date { get; set; }
    public bool Planned { get; set; }
    public bool Present { get; set; }
    public LocalTime? Arrival { get; set; }
    public LocalTime? Departure { get; set; }
    public bool Lunch { get; set; }
    public bool Snack { get; set; }
    public string? Absence { get; set; }
}
