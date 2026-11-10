namespace Nid.Income.Contracts;

/// <summary>2C1 spec.</summary>
public static class IncomeCodes
{
    public const string UnknownKind = "income.unknown_kind";
    public const string UnknownCadence = "income.unknown_cadence";
    public const string InvalidSource = "income.invalid_source";
    public const string InvalidPhase = "income.invalid_phase";
    public const string InvalidTaxable = "income.invalid_taxable";
    public const string PhaseOverlap = "income.phase_overlap";
    public const string SourceEnded = "income.source_ended";
    public const string SourceOpen = "income.source_open";
    public const string NoRunningPhase = "income.no_running_phase";
    public const string InvalidPeriod = "income.invalid_period";
    /// <summary>Issue #12: the overview's year, outside the years it reads or without the period year.</summary>
    public const string InvalidYear = "income.invalid_year";
    public const string PhaseConflict = "income.phase_conflict";
    public const string SourceWithoutPhase = "income.source_without_phase";
    public const string GapSoon = "income.gap_soon";
}
