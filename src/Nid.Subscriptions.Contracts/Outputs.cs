using NodaTime;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.Contracts;

/// <summary>Where a charge came from.</summary>
public abstract record ChargeOrigin
{
    private ChargeOrigin() { }

    public sealed record FromPhase(BillingPhaseId PhaseId, int Cycle) : ChargeOrigin;

    public sealed record FromPurchase(PurchaseId PurchaseId) : ChargeOrigin;
}

/// <summary>Where a coverage period came from.</summary>
public abstract record CoverageOrigin
{
    private CoverageOrigin() { }

    public sealed record FromPhase(BillingPhaseId PhaseId, int Cycle) : CoverageOrigin;

    public sealed record FromGrant(PurchaseId PurchaseId, int GrantIndex) : CoverageOrigin;
}

/// <summary>
/// A period of access, half-open <c>[Start, EndBefore)</c>. Uses two dates rather than a
/// <see cref="DateRange"/> because coverage always ends, while DateRange's end is optional.
/// </summary>
public sealed record CoveragePeriod(
    LocalDate Start,
    LocalDate EndBefore,
    CoverageSource Source,
    CoverageOrigin Origin,
    bool IsProjected)
{
    public int Days => Period.DaysBetween(Start, EndBefore);

    public bool Contains(LocalDate date) => date >= Start && date < EndBefore;

    public bool IsFree => Source != CoverageSource.Paid;
}

/// <summary>
/// How a charge's cost basis is spread over its covered days. The first <paramref name="HighDays"/>
/// days each take one extra unit of <c>10^-Money.MaxDecimals</c>, so the days tile the basis exactly.
/// </summary>
public sealed record CostAllocation(Money Basis, int Days, decimal PerDay, int HighDays);

/// <summary>Money leaving the account once.</summary>
public sealed record Charge(
    LocalDate Date,
    Money Price,
    Payer Payer,
    ChargeOrigin Origin,
    IReadOnlyList<CoveragePeriod> Coverage,
    bool IsProjected,
    CostAllocation Allocation);

/// <summary>Totals, one per currency. The engine never converts: fx_rate arrives with 1C.</summary>
public sealed record CostBreakdown(IReadOnlyList<Money> Totals)
{
    public static CostBreakdown Empty { get; } = new([]);

    public Money? For(Currency currency)
    {
        foreach (var total in Totals)
            if (total.Currency == currency)
                return total;

        return null;
    }
}

/// <summary>Something a human should look at. <paramref name="Code"/> is stable; the message is not.</summary>
public sealed record Finding(string Code, string Message, string? Reference);

/// <summary>
/// Part of a cost, dated by the charge it comes from: what conversion needs, since a charge converts at the rate
/// of its own date (1C2a design §5.1). <paramref name="Amount"/> is exact, not rounded.
/// </summary>
public sealed record CostPiece(LocalDate ChargeDate, Currency Currency, decimal Amount, bool IsProjected);

public sealed record Timeline(
    IReadOnlyList<CoveragePeriod> Coverage,
    IReadOnlyList<Charge> Charges,
    IReadOnlyList<Finding> Warnings);
