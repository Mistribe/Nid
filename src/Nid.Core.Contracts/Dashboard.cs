using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>
/// 2D1 spec §4: what a module holds of the reader's net worth, as exact per-currency sums. <c>StartValues</c>,
/// <c>NetContributions</c> and <c>Gains</c> cover only the envelopes with a value both on or before 1 January and today.
/// 2D2a spec §4: <c>Holdings</c> lists each counted envelope, with or without a value.
/// </summary>
public sealed record NetWorthPart(
    int Envelopes, int WithoutValue, int Stale, LocalDate? OldestAsOf,
    IReadOnlyDictionary<Currency, decimal> Values, IReadOnlyDictionary<Currency, decimal> StartValues,
    IReadOnlyDictionary<Currency, decimal> NetContributions, IReadOnlyDictionary<Currency, decimal> Gains,
    IReadOnlyList<Holding> Holdings);

/// <summary>2D2a spec §4: one visible open envelope of the net worth; <c>Value</c> null when it has none (or its facts cannot be read).</summary>
public sealed record Holding(
    ItemId Item, string Module, string Name, Money? Value, LocalDate? AsOf, bool Stale, IReadOnlyList<OwnerShare> Owners);

/// <summary>2D1 spec §4: this month's amortized and cash cost of the reader's visible active subscriptions.</summary>
public sealed record CommitmentsPart(
    int Subscriptions, IReadOnlyDictionary<Currency, decimal> Amortized, IReadOnlyDictionary<Currency, decimal> Cash);

/// <summary>
/// 2D1 spec §4: the monthly equivalent of the phases running today, per currency. A contribution counting a source in
/// <c>Sources</c> puts an entry (0 when nothing runs) in that source's currency, so a declared source never reads « aucun revenu déclaré ».
/// </summary>
public sealed record IncomePart(int Sources, IReadOnlyDictionary<Currency, decimal> MonthlyEquivalent);

/// <summary>A module holding part of the net worth; the dashboard sums every registered one (stage 3 adds real estate).</summary>
public interface INetWorthContribution
{
    Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);
}

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
