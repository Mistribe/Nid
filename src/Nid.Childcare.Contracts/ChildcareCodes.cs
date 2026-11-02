namespace Nid.Childcare.Contracts;

public static class ChildcareCodes
{
    public const string ChildNotDependent = "childcare.child_not_dependent";
    public const string ScheduleEmpty = "childcare.schedule_empty";
    public const string ScheduleDuplicateDay = "childcare.schedule_duplicate_day";
    public const string DepartureBeforeArrival = "childcare.departure_before_arrival";
    public const string WeeksOutOfRange = "childcare.programmed_weeks_out_of_range";
    public const string GapNotMonday = "childcare.gap_not_monday";
    public const string GapWeeksOutOfRange = "childcare.gap_weeks_out_of_range";
    public const string GapOverlap = "childcare.gap_overlap";
    public const string GapsTooMany = "childcare.gaps_too_many";
    public const string RevisionNotAfter = "childcare.revision_not_after";
    public const string EndBeforeTerms = "childcare.end_before_terms";
    public const string Closed = "childcare.closed";
    public const string Open = "childcare.open";
    public const string Conflict = "childcare.conflict";
}
