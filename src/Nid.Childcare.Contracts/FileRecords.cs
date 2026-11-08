using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.Childcare.Contracts;

// A member the file may omit is `set`, not `init`: the source-generated reader of .NET 10 and earlier (NativeAOT) gives an
// omitted init-only property its type's default, not its initializer.

/// <summary>
/// Childcare decision 24: an arrangement of <c>nid.data</c> with its carer, dated terms (each with its weekly schedule), weeks
/// without care, owners and visibility. Dates, times and months stay strings, the section parses them; amounts read as decimals.
/// </summary>
public sealed record ArrangementRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>A <c>persons</c> ref of kind dependent.</summary>
    public required string Child { get; init; }

    public required CarerRecord Carer { get; init; }
    public required IReadOnlyList<TermsRecord> Terms { get; init; }
    public IReadOnlyList<GapRecord> Gaps { get; set; } = [];
    public IReadOnlyList<WeekOverrideRecord>? WeekOverrides { get; set; }
    public string Visibility { get; set; } = "shared";

    /// <summary>#294: <c>view</c> when omitted, <c>edit</c> lets every adult write the shared item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShareLevel { get; set; }

    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public bool Closed { get; set; }
}

public sealed record CarerRecord
{
    public required string Name { get; init; }
    public string? Pajemploi { get; set; }
    public string? Approval { get; set; }
}

/// <summary>One revision; <c>overtime_pct</c> in percent (10 = 10 %), as the API.</summary>
public sealed record TermsRecord
{
    public required string From { get; init; }
    public string? To { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal NetHourlyRate { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? GrossHourlyRate { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal OvertimePct { get; init; }

    [JsonPropertyName("upkeep_per_9h")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal UpkeepPer9h { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? MealPrice { get; set; }

    /// <summary>The old form, one template week: read, never written. Either it and <c>schedule</c>, or <c>week_types</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProgrammedWeeks { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SlotRecord>? Schedule { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WeekTypeRecord>? WeekTypes { get; set; }
}

/// <summary>A template week of a revision: its name, its weeks of the year and its schedule.</summary>
public sealed record WeekTypeRecord
{
    public required string Name { get; init; }
    public required int Weeks { get; init; }
    public required IReadOnlyList<SlotRecord> Schedule { get; init; }
}

/// <summary>An exception: the week of this Monday uses the week type of this rank (0-based).</summary>
public sealed record WeekOverrideRecord
{
    public required string Monday { get; init; }
    public required int WeekType { get; init; }
}

/// <summary>ISO day (1 = Monday), times <c>HH:mm</c>.</summary>
public sealed record SlotRecord
{
    public required int Day { get; init; }
    public required string Arrival { get; init; }
    public required string Departure { get; init; }
    public bool Meal { get; set; }
}

public sealed record GapRecord
{
    public required string Monday { get; init; }
    public required int Weeks { get; init; }
}

/// <summary>Childcare decision 24: a validated month of an arrangement of the file, its days and its frozen figures.</summary>
public sealed record StatementRecord
{
    /// <summary>The <c>id</c> of a <c>childcare_arrangements</c> entity.</summary>
    public required string Arrangement { get; init; }

    /// <summary><c>yyyy-MM</c>.</summary>
    public required string Month { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal Mileage { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Cmg { get; init; }

    /// <summary>#30: the contributions the Pajemploi summary leaves to the household; absent for none.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Contributions { get; set; }

    public DeclaredRecord? Declared { get; set; }

    public required IReadOnlyList<DayRecord> Days { get; init; }
    public required FiguresRecord Figures { get; init; }
}

/// <summary>Decision 30: the declaration's fields entered by hand.</summary>
public sealed record DeclaredRecord
{
    public int? NormalHours { get; set; }
    public int? ActivityDays { get; set; }
    public int? PaidLeaveDays { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? ComplementaryHours { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? OvertimeHours { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? NetPay { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Upkeep { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Meals { get; set; }
}

/// <summary>A day; <c>absence</c> snake_case (<c>child_sick</c>).</summary>
public sealed record DayRecord
{
    public required string Date { get; init; }
    public bool Planned { get; set; }
    public bool Present { get; set; }
    public string? Arrival { get; set; }
    public string? Departure { get; set; }
    public bool Meal { get; set; }
    public string? Absence { get; set; }
}

/// <summary>The frozen figures of the month (spec §3), re-checked on import (decision 25).</summary>
public sealed record FiguresRecord
{
    public required int NormalHours { get; init; }
    public required int ActivityDays { get; init; }
    public int? PaidLeaveDays { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal ComplementaryHours { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal OvertimeHours { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal NetPay { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Upkeep { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Meals { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal BasePay { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Deduction { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal ExtraPay { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal PaidLeavePay { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Cost { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal TaxBase { get; init; }

    public required int ChildSickDeducted { get; init; }
}
