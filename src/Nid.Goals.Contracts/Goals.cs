namespace Nid.Goals.Contracts;

public static class GoalKinds
{
    public const string Savings = "savings";
    public const string EmergencyFund = "emergency_fund";
    public static readonly IReadOnlyList<string> All = [Savings, EmergencyFund];
}

public static class GoalStatuses
{
    public const string Reached = "reached";
    public const string OnTrack = "on_track";
    public const string Behind = "behind";
    public const string PastDue = "past_due";
    public const string NoDate = "no_date";
}

public static class GoalFlags
{
    public const string Partial = "partial";
    public const string MissingCurrency = "missing_currency";
    public const string ClosedEnvelope = "closed_envelope";
    public const string NoCommitments = "no_commitments";
    public const string NoValue = "no_value";
    public const string Stale = "stale";
    public const string OverAllocated = "over_allocated";
}
