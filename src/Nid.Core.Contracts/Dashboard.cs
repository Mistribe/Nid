using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>
/// 2D1 spec §4: what a module holds of the reader's net worth, as exact per-currency sums. <c>StartValues</c>,
/// <c>NetContributions</c> and <c>Gains</c> cover only the envelopes with a value both on or before 1 January and today.
/// 2D2a spec §4: <c>Holdings</c> lists each counted envelope, with or without a value.
/// 2D2b spec §4: <c>WithoutStart</c> counts the envelopes valued today whose start value is missing or older than their staleness threshold at 1 January; they are left out of the change.
/// </summary>
public sealed record NetWorthPart(
    int Envelopes, int WithoutValue, int Stale, LocalDate? OldestAsOf,
    IReadOnlyDictionary<Currency, decimal> Values, IReadOnlyDictionary<Currency, decimal> StartValues,
    IReadOnlyDictionary<Currency, decimal> NetContributions, IReadOnlyDictionary<Currency, decimal> Gains,
    IReadOnlyList<Holding> Holdings, int WithoutStart);

/// <summary>
/// 2D2a spec §4: one visible open envelope of the net worth; <c>Value</c> null when it has none (or its facts cannot be read).
/// Issue #104: <c>Slices</c> splits <c>Value</c> by asset class and liquidity; null lets the dashboard classify it by module.
/// Holding prices spec §5.4: <c>Estimated</c> when <c>Value</c> and <c>AsOf</c> are an estimate; <c>Stale</c> still follows the valuation
/// (D17), dated by <c>ValuedOn</c>, the latest valuation's date (null: <c>AsOf</c>).
/// </summary>
public sealed record Holding(
    ItemId Item, string Module, string Name, Money? Value, LocalDate? AsOf, bool Stale, IReadOnlyList<OwnerShare> Owners,
    IReadOnlyList<AllocationSlice>? Slices = null, bool Estimated = false, LocalDate? ValuedOn = null);

/// <summary>Issue #104: a part of a holding's value, in its currency; <c>Approximate</c> when the class is guessed from a kind.</summary>
public sealed record AllocationSlice(string Class, string Liquidity, decimal Amount, bool Approximate);

/// <summary>Issue #104: the asset classes of the allocation, in display order; a debt is no class (it is shown apart).</summary>
public static class AllocationClasses
{
    public const string Cash = "cash";
    public const string MoneyMarket = "money_market";
    public const string Bond = "bond";
    public const string Equity = "equity";
    public const string Mixed = "mixed";
    public const string CapitalGuaranteed = "capital_guaranteed";
    public const string RealEstate = "real_estate";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Cash, MoneyMarket, Bond, Equity, Mixed, CapitalGuaranteed, RealEstate, Other];
}

/// <summary>Issue #104: how soon a value can be spent — now, within days (a sale or a withdrawal), or not before an event (retirement, a sale of a property).</summary>
public static class AllocationLiquidity
{
    public const string Available = "available";
    public const string Days = "days";
    public const string Locked = "locked";

    public static readonly IReadOnlyList<string> All = [Available, Days, Locked];
}

/// <summary>
/// 2D1 spec §4: this month's amortized and cash cost of one source of the reader's visible commitments. 3E1 spec §3:
/// <c>Source</c> is one of <see cref="CommitmentSources"/>; <c>Count</c> the items it counted, so a source counting items
/// that cost nothing this month still reads « aucun coût ce mois-ci », not « aucun engagement ».
/// </summary>
public sealed record CommitmentsPart(
    string Source, int Count, IReadOnlyDictionary<Currency, decimal> Amortized, IReadOnlyDictionary<Currency, decimal> Cash);

/// <summary>3E1 spec §3: the sources of the commitments, as the wire names them.</summary>
public static class CommitmentSources
{
    public const string Subscriptions = "subscriptions";
    public const string Loans = "loans";
    public const string PropertyCosts = "property_costs";
    public const string Tax = "tax";
}

/// <summary>
/// 2D1 spec §4: the monthly equivalent of the phases running today, per currency. A contribution counting a source in
/// <c>Sources</c> puts an entry (0 when nothing runs) in that source's currency, so a declared source never reads « aucun revenu déclaré ».
/// </summary>
public sealed record IncomePart(int Sources, IReadOnlyDictionary<Currency, decimal> MonthlyEquivalent);

/// <summary>A module holding part of the net worth; the dashboard sums every registered one (stage 3 adds real estate).</summary>
public interface INetWorthContribution
{
    Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);

    /// <summary>2D2b spec §4: one point per date of <paramref name="dates"/>, over the visible holdings, closed ones included until their closing.</summary>
    Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct);
}

/// <summary>2D2b spec §4: a module's part of the curve at one date; <c>Envelopes</c> counts the holdings with a reading on or before it.</summary>
public sealed record NetWorthPoint(
    LocalDate On, IReadOnlyDictionary<Currency, decimal> Values, IReadOnlyDictionary<Currency, decimal> NetContributions, int Envelopes);

public interface ICommitmentsContribution
{
    Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);
}

public interface IIncomeContribution
{
    Task<Result<IncomePart>> IncomeAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);
}

/// <summary>2D2a spec §4: a dated event of « À venir » (or, for <see cref="UpcomingKinds.IncomeGapSoon"/>, of « À faire »). <c>Amount</c> stays in its own currency.</summary>
public sealed record UpcomingEvent(LocalDate On, string Kind, ItemId Item, string Module, string Name, string? Label, Money? Amount);

public static class UpcomingKinds
{
    public const string SubscriptionRenewal = "subscription_renewal";
    public const string SubscriptionExpiry = "subscription_expiry";
    public const string IncomePhaseEnd = "income_phase_end";
    public const string IncomeGapSoon = "income_gap_soon";

    // 3E1 spec §4.
    public const string LoanPhaseEnd = "loan_phase_end";
    public const string LoanEnd = "loan_end";
    public const string ValuationDue = "valuation_due";
    public const string PropertyCostDue = "property_cost_due";
    public const string TenancyEnd = "tenancy_end";

    // Issue #34 §2.7.
    public const string HoldingMaturity = "holding_maturity";

    // Tax-per spec §4.6: on 31 December, the PER ceiling left to pay in (a person's, or the household's under pooling) and the part lost.
    public const string PerDeadline = "per_deadline";
    public const string PerCeilingExpiring = "per_ceiling_expiring";
}

/// <summary>A module's events dated in [<c>today</c>, <c>until</c>], over the items the reader sees in the scope.</summary>
public interface IUpcomingEvents
{
    Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct);
}
