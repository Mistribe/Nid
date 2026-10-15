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

/// <summary>2D2a spec §4: one visible open envelope of the net worth; <c>Value</c> null when it has none (or its facts cannot be read).</summary>
public sealed record Holding(
    ItemId Item, string Module, string Name, Money? Value, LocalDate? AsOf, bool Stale, IReadOnlyList<OwnerShare> Owners);

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
}

/// <summary>A module's events dated in [<c>today</c>, <c>until</c>], over the items the reader sees in the scope.</summary>
public interface IUpcomingEvents
{
    Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct);
}
