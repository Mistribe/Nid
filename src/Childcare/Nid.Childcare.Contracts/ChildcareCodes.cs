namespace Nid.Childcare.Contracts;

public static class ChildcareCodes
{
    public const string ChildNotDependent = "childcare.child_not_dependent";
    public const string ScheduleEmpty = "childcare.schedule_empty";
    public const string ScheduleDuplicateDay = "childcare.schedule_duplicate_day";
    public const string DepartureBeforeArrival = "childcare.departure_before_arrival";
    public const string WeeksOutOfRange = "childcare.programmed_weeks_out_of_range";
    public const string WeekTypesEmpty = "childcare.week_types_empty";
    public const string WeekTypeWeeksOutOfRange = "childcare.week_type_weeks_out_of_range";
    public const string WeekTypeNameInvalid = "childcare.week_type_name_invalid";
    public const string OverrideInvalidMonday = "childcare.override_invalid_monday";
    public const string OverrideUnknownWeekType = "childcare.override_unknown_week_type";
    public const string TermsBothFormats = "childcare.terms_both_formats";
    public const string GapNotMonday = "childcare.gap_not_monday";
    public const string GapWeeksOutOfRange = "childcare.gap_weeks_out_of_range";
    public const string GapOverlap = "childcare.gap_overlap";
    public const string GapsTooMany = "childcare.gaps_too_many";
    public const string DaysIncomplete = "childcare.days_incomplete";
    public const string AbsenceRequired = "childcare.absence_required";
    public const string AbsenceNotAllowed = "childcare.absence_not_allowed";
    public const string BelowMinimumGrossRate = "childcare.below_minimum_gross_rate";
    public const string BelowMinimumNetRate = "childcare.below_minimum_net_rate";
    public const string BelowMinimumUpkeep = "childcare.below_minimum_upkeep";
    public const string DayReplanned = "childcare.day_replanned";
    public const string SummaryInvalid = "childcare.summary_invalid";
    public const string SummaryTooManyDays = "childcare.summary_too_many_days";
    public const string StatementValidated = "childcare.statement_validated";
    public const string StatementIncomplete = "childcare.statement_incomplete";
    public const string NextValidated = "childcare.next_validated";
    public const string PreviousNotValidated = "childcare.previous_not_validated";
    public const string EndBeforeValidated = "childcare.end_before_validated";
    public const string CmgRequired = "childcare.cmg_required";
    public const string StatementConflict = "childcare.statement_conflict";
    public const string FiguresDiffer = "childcare.figures_differ";
    public const string UnknownArrangement = "childcare.unknown_arrangement";
    public const string StatementTwice = "childcare.statement_twice";
    public const string MonthOutOfContract = "childcare.month_out_of_contract";
    public const string RevisionNotAfter = "childcare.revision_not_after";
    public const string EndBeforeTerms = "childcare.end_before_terms";
    public const string GrossRateRequired = "childcare.gross_rate_required";
    public const string NotifiedOutOfContract = "childcare.notified_out_of_contract";
    public const string Closed = "childcare.closed";
    public const string Open = "childcare.open";
    public const string Conflict = "childcare.conflict";
}
