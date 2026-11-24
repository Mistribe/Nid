using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>Holdings spec §2.1: the wire values of a holding's kind and asset class.</summary>
public static class HoldingWire
{
    public static readonly IReadOnlyList<string> Kinds = ["euro_fund", "fund", "etf", "equity", "bond", "structured", "cash", "scpi", "other"];

    public static readonly IReadOnlyList<string> AssetClasses = ["equity", "bond", "money_market", "mixed", "real_estate", "capital_guaranteed", "other"];

    /// <summary>A euro fund and cash are a capital: no units, no unit price.</summary>
    public static bool HasUnits(string kind) => kind is not ("euro_fund" or "cash");

    /// <summary>Issue #180: a euro fund, cash and an SCPI's buildings have no market exposure to break down by region and sector.</summary>
    public static bool IsMarket(string kind) => kind is not ("euro_fund" or "cash" or "scpi");
}

/// <summary>Issue #34 §2.3: a movement's kind, its direction, and whether it is money in or out of the holding.</summary>
public static class MovementKinds
{
    public const string AllocationIn = "allocation_in";
    public const string AllocationOut = "allocation_out";
    public const string SwitchIn = "switch_in";
    public const string SwitchOut = "switch_out";
    public const string Maturity = "maturity";
    public const string Fee = "fee";
    public const string Interest = "interest";
    public const string Income = "income";

    public static readonly IReadOnlyList<string> All = [AllocationIn, AllocationOut, SwitchIn, SwitchOut, Maturity, Fee, Interest, Income];

    /// <summary>Money in of the holding's performance.</summary>
    public static bool IsMoneyIn(string kind) => kind is AllocationIn or SwitchIn;

    /// <summary>Money out of the holding's performance.</summary>
    public static bool IsMoneyOut(string kind) => kind is AllocationOut or SwitchOut or Maturity;

    /// <summary>Units come in (bought, reinvested); the others take units out.</summary>
    public static bool IsIn(string kind) => kind is AllocationIn or SwitchIn or Interest or Income;
}

/// <summary>One line of an envelope (holdings spec §2.1): a fund, a share, the euro fund, a structured product, cash.</summary>
public sealed record EnvelopeHolding(
    Guid Id, ItemId Envelope, string Name, string? Isin, string Kind, string? AssetClass, string? Issuer, int? RiskIndicator,
    LocalDate? OpenedOn, LocalDate? MaturesOn, decimal? EntryCostPct, LocalDate? ClosedOn, string? Note, bool AutoPrice = true,
    int? EntitlementDelayMonths = null);

/// <summary>A holding in a snapshot: <see cref="Value"/> as printed, in the envelope's currency; units and prices with 6 decimals.</summary>
public sealed record SnapshotLine(Guid HoldingId, decimal? Units, decimal? UnitPrice, decimal Value, decimal? CostBasisPerUnit);

/// <summary>The composition of an envelope at a date (holdings spec §2.2): one per date.</summary>
public sealed record HoldingSnapshot(Guid Id, ItemId Envelope, LocalDate AsOf, string? SourceLabel, bool Complete, IReadOnlyList<SnapshotLine> Lines);

/// <summary>
/// A dated movement on one holding (issue #34 §2.3): <see cref="Amount"/> is above zero, its direction comes from the kind; units are
/// signed (bought +, sold or deducted −). The two legs of a switch share <see cref="SwitchRef"/>.
/// </summary>
public sealed record HoldingMovement(
    Guid Id, ItemId Envelope, Guid HoldingId, LocalDate OccurredOn, string Kind, decimal? Units, decimal? UnitPrice, decimal Amount,
    Guid? EnvelopeFlowId, string? SwitchRef, string? Label, string? ExternalRef);

/// <summary>Issue #34 §3, a holding at its latest valued snapshot on or before the date: money in and out, gain, XIRR, explicit fees.</summary>
public sealed record HoldingFigures(
    LocalDate AsOf, Money Value, Money MoneyIn, Money MoneyOut, Money Gain, decimal? GainPct, decimal? AnnualReturnPct,
    Money ExplicitFees, Money GainBeforeFees);

public static class HoldingWarnings
{
    public const string ValueMismatch = "holding_value_mismatch";
    public const string SumMismatch = "holdings_sum_mismatch";
    public const string AllocationMismatch = "allocation_mismatch";
    public const string SwitchUnbalanced = "switch_unbalanced";
    public const string CostReportInconsistent = "cost_report_inconsistent";
    public const string UnexplainedUnits = "unexplained_units";
    public const string UcShareBelowFloor = "uc_share_below_floor";
}

/// <summary>
/// A warning about a snapshot or movements, on one holding when <see cref="HoldingId"/> is set, on a date when <see cref="On"/> is.
/// <c>unexplained_units</c> carries the <see cref="Units"/> no movement explains and, when units went missing, the <see cref="ProposedFee"/>
/// at the later unit price (never written).
/// </summary>
public sealed record HoldingWarning(string Code, Guid? HoldingId = null, LocalDate? On = null, decimal? Units = null, Money? ProposedFee = null);

/// <summary>
/// Issue #34 §2.5: a holding's yearly gross return, total fees and net return, in percent, as stated on the annual document (art. A.522-1);
/// never derived from snapshots.
/// </summary>
public sealed record CostReport(
    Guid HoldingId, int Year, decimal GrossReturnPct, decimal TotalFeesPct, decimal NetReturnPct, decimal? ContractFeesPct, decimal? SupportFeesPct,
    decimal? RetrocessionPct, decimal? NetAnnualized5yPct, string? SourceLabel);
