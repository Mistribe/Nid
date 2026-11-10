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
/// that cost nothing this month still reads « aucun coût ce mois-ci », not « aucun engagement ». <c>Estimated</c>: a figure not yet
/// known (childcare spec decision 20: a month not yet validated).
/// </summary>
public sealed record CommitmentsPart(
    string Source, int Count, IReadOnlyDictionary<Currency, decimal> Amortized, IReadOnlyDictionary<Currency, decimal> Cash, bool Estimated = false);

/// <summary>3E1 spec §3: the sources of the commitments, as the wire names them.</summary>
public static class CommitmentSources
{
    public const string Subscriptions = "subscriptions";
    public const string Loans = "loans";
    public const string PropertyCosts = "property_costs";
    public const string Tax = "tax";
    public const string Childcare = "childcare";
}

/// <summary>
/// 2D1 spec §4: the monthly equivalent of the phases running today, per currency. A contribution counting a source in
/// <c>Sources</c> puts an entry (0 when nothing runs) in that source's currency, so a declared source never reads « aucun revenu déclaré ».
/// <c>ReaderShare</c> is the reader's part of it by each source's shares (issue #402: the « Moi » debt ratio); null when not given.
/// </summary>
public sealed record IncomePart(
    int Sources, IReadOnlyDictionary<Currency, decimal> MonthlyEquivalent, IReadOnlyDictionary<Currency, decimal>? ReaderShare = null);

/// <summary>A module holding part of the net worth; the dashboard sums every registered one (stage 3 adds real estate).</summary>
public interface INetWorthContribution
{
    /// <summary>Issue #415: the module this part is, as the causes of a month's change name it.</summary>
    string Module => "other";

    Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);

    /// <summary>2D2b spec §4: one point per date of <paramref name="dates"/>, over the visible holdings, closed ones included until their closing.</summary>
    Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct);

    /// <summary>
    /// Projection spec §2.1: the contributions minus the withdrawals dated in [<paramref name="from"/>, <paramref name="to"/>], per currency,
    /// over the visible holdings, closed ones included; openings are not savings. A module with nothing to save into returns an empty map.
    /// </summary>
    Task<Result<IReadOnlyDictionary<Currency, decimal>>> NetContributionsAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct);
}

/// <summary>
/// 2D2b spec §4: a module's part of the curve at one date; <c>Envelopes</c> counts the holdings with a reading on or before it.
/// Issue #12: <c>Moved</c>, cumulative up to the date, is what entered or left the module by a purchase or a sale (a property's
/// price, − a loan's principal, − what a sold item was worth); null for a module without any.
/// </summary>
public sealed record NetWorthPoint(
    LocalDate On, IReadOnlyDictionary<Currency, decimal> Values, IReadOnlyDictionary<Currency, decimal> NetContributions, int Envelopes,
    IReadOnlyDictionary<Currency, decimal>? Moved = null);

public interface ICommitmentsContribution
{
    Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);
}

public interface IIncomeContribution
{
    /// <summary>Issue #415: the module this part is, as the causes of a month's change name it.</summary>
    string Module => "other";

    Task<Result<IncomePart>> IncomeAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);

    /// <summary>
    /// Projection spec §3.2, §5.1: the monthly equivalent over the next <paramref name="months"/> months, per currency;
    /// <c>Monthly[k]</c> is read on the last day of month (this month + k), <c>Monthly[0]</c> on <paramref name="today"/> (= <see cref="IncomeAsync"/>).
    /// </summary>
    Task<Result<IncomeTimeline>> TimelineAsync(PersonId reader, VisibilityScope scope, LocalDate today, int months, CancellationToken ct);
}

/// <summary>An open source whose phase ends without a successor: the timeline keeps it running (projection spec §3.2) and names it so the screen can warn.</summary>
public sealed record ExtendedSource(ItemId Source, string Name);

/// <summary>An amount of <c>Monthly[k]</c> and the month it is indexed from (D8): 0 for what runs today or is carried on, else the months from this month to its start.</summary>
public sealed record TimelinePart(Currency Currency, decimal Amount, int IndexedFrom);

/// <summary>
/// <c>Monthly</c> has months + 1 entries (see <see cref="IIncomeContribution.TimelineAsync"/>); <c>Extended</c> lists the sources whose income was carried on.
/// <c>Parts[k]</c> splits <c>Monthly[k]</c> by phase; null reads every amount as indexed from 0.
/// </summary>
public sealed record IncomeTimeline(IReadOnlyList<IReadOnlyDictionary<Currency, decimal>> Monthly, IReadOnlyList<ExtendedSource> Extended,
    IReadOnlyList<IReadOnlyList<TimelinePart>>? Parts = null);

/// <summary>2D2a spec §4: a dated event of « À venir » (or, for <see cref="UpcomingKinds.IncomeGapSoon"/>, of « À faire »). <c>Amount</c> stays in its own currency.</summary>
public sealed record UpcomingEvent(LocalDate On, string Kind, ItemId Item, string Module, string Name, string? Label, Money? Amount);

public static class UpcomingKinds
{
    public const string SubscriptionRenewal = "subscription_renewal";
    public const string SubscriptionExpiry = "subscription_expiry";
    public const string SubscriptionCancelDeadline = "subscription_cancel_deadline";
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

    // Issue #400: an envelope's next tax date (8 years of a life insurance, 5 of a PEA, a PER's retirement); the label is its code.
    public const string TaxMilestone = "tax_milestone";

    // Tax-per spec §4.6: on 31 December, the PER ceiling left to pay in (a person's, or the household's under pooling) and the part lost.
    public const string PerDeadline = "per_deadline";
    public const string PerCeilingExpiring = "per_ceiling_expiring";

    // Issue #409, the yearly tax calendar (TaxCalendar): the household's events, then a property's with a « taxe foncière » charge.
    public const string TaxFilingDeadline = "tax_filing_deadline";
    public const string TaxNewRate = "tax_new_rate";
    public const string TaxBalanceDue = "tax_balance_due";
    public const string PropertyTaxDue = "property_tax_due";
}

/// <summary>A module's events dated in [<c>today</c>, <c>until</c>], over the items the reader sees in the scope.</summary>
public interface IUpcomingEvents
{
    Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct);
}
