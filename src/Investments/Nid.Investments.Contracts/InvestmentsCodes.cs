namespace Nid.Investments.Contracts;

/// <summary>2B1a spec §6.</summary>
public static class InvestmentsCodes
{
    public const string UnknownKind = "investments.unknown_kind";
    public const string UnknownCadence = "investments.unknown_cadence";
    public const string InvalidEnvelope = "investments.invalid_envelope";
    public const string EnvelopeClosed = "investments.envelope_closed";
    public const string EnvelopeOpen = "investments.envelope_open";
    public const string TransferSameEnvelope = "investments.transfer_same_envelope";
    public const string TransferCurrencyMismatch = "investments.transfer_currency_mismatch";
    public const string InvalidPeriod = "investments.invalid_period";
    public const string RoutineEmptyLine = "investments.routine_empty_line";
    public const string RoutineDuplicate = "investments.routine_duplicate";
    public const string RoutineValueExists = "investments.routine_value_exists";
    public const string RoutineInvalidAmount = "investments.routine_invalid_amount";
    public const string InvalidHolding = "investments.invalid_holding";
    public const string UnknownHoldingKind = "investments.unknown_holding_kind";
    public const string InvalidIsin = "investments.invalid_isin";
    public const string HoldingClosed = "investments.holding_closed";
    public const string HoldingInUse = "investments.holding_in_use";
    public const string HoldingConflict = "investments.holding_conflict";
    public const string InvalidSnapshot = "investments.invalid_snapshot";
    public const string SnapshotExists = "investments.snapshot_exists";
    public const string InvalidMovement = "investments.invalid_movement";
    public const string InvalidCostReport = "investments.invalid_cost_report";
    public const string InvalidFeeReferences = "investments.invalid_fee_references";
    public const string InvalidIncomeCategory = "investments.invalid_income_category";
    /// <summary>#183: an employer's share (match, profit-sharing, participation) off an employee plan's contribution.</summary>
    public const string InvalidContributionCategory = "investments.invalid_contribution_category";
    public const string TransferLeg = "investments.transfer_leg";
    /// <summary>#16: a flow kept from a bank line (<c>bank:</c> ref) follows its line, never a hand edit.</summary>
    public const string BankFlow = "investments.bank_flow";
    public const string AboveCeiling = "investments.above_ceiling";
    /// <summary>#182: a PEE or a collective PER belongs to one employee, so exactly one adult owns it.</summary>
    public const string PlanOwner = "investments.plan_owner";
}
