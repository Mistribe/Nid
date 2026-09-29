using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary><c>core-data-model.md</c> §5.3: the meaning is a contract; the direction comes from the kind, never the sign.</summary>
public enum FlowKind
{
    Contribution,
    Withdrawal,
    Opening,
    Income,
    Expense,
}

/// <summary><c>core-data-model.md</c> §5.1. <c>bank_api</c> and <c>document</c> arrive with stages 4 and 5.</summary>
public enum FactSource
{
    Manual,
    Import,
}

public sealed record NewValuation(ItemId Item, LocalDate AsOf, Money Value, string? Note, FactSource Source = FactSource.Manual);

public sealed record Valuation(Guid Id, ItemId Item, LocalDate AsOf, Money Value, string? Note);

/// <summary><see cref="Value"/> is null when the item has no value at that date: "unknown", never zero.</summary>
public sealed record LatestValuation(Valuation? Value);

public sealed record NewFlow(
    ItemId Item, LocalDate OccurredOn, FlowKind Kind, Money Amount,
    string? Category, string? ExternalRef, string? Note, FactSource Source = FactSource.Manual);

public sealed record Flow(
    Guid Id, ItemId Item, LocalDate OccurredOn, FlowKind Kind, Money Amount,
    string? Category, string? ExternalRef, string? Note);

/// <summary>
/// 2A spec §4. Every call is made as the ambient actor, who must be a person: writes need write access to the item,
/// reads read access. A write joins the caller's unit of work when one is open, or runs in a transaction of its own.
/// A refused or failed write — a failure result or an exception — leaves nothing of itself in the caller's transaction
/// or change tracker: whatever it had saved is rolled back to a savepoint taken before it; committing the caller's own
/// work afterwards is the caller's choice. A write saves the Core context: any other Core change the caller left
/// pending is flushed with it and, if the write then fails, rolled back with it though the tracker takes it as saved;
/// so a caller saves its own Core changes before a fact write.
/// </summary>
public interface IValuations
{
    /// <summary>Creates the value of a date, or replaces the current one of that date (the old row retired, the new one superseding it).</summary>
    Task<Result<Valuation>> RecordAsync(NewValuation valuation, CancellationToken ct);

    Task<Result> VoidAsync(ItemId item, Guid id, CancellationToken ct);

    /// <summary>The last current value with <c>as_of</c> on or before <paramref name="on"/>.</summary>
    Task<Result<LatestValuation>> LatestAsync(ItemId item, LocalDate on, CancellationToken ct);

    /// <summary>Current values whose <c>as_of</c> is in <paramref name="range"/>, by <c>as_of</c>.</summary>
    Task<Result<IReadOnlyList<Valuation>>> SeriesAsync(ItemId item, DateRange range, CancellationToken ct);
}

/// <summary>
/// 2A spec §4. Same actor, transaction and change-tracker rules as <see cref="IValuations"/>: a refused or failed
/// write leaves nothing in the caller's transaction or tracker (a failed supersede keeps the old flow current), and a
/// write saves the Core context.
/// </summary>
public interface IFlows
{
    Task<Result<Flow>> RecordAsync(NewFlow flow, CancellationToken ct);

    /// <summary>Retires the flow and records its replacement, which must be on the same item, in one transaction.</summary>
    Task<Result<Flow>> SupersedeAsync(ItemId item, Guid id, NewFlow replacement, CancellationToken ct);

    Task<Result> VoidAsync(ItemId item, Guid id, CancellationToken ct);

    /// <summary>Current flows in <paramref name="range"/>, by <c>occurred_on</c>, then creation, then id; all kinds when <paramref name="kinds"/> is null.</summary>
    Task<Result<IReadOnlyList<Flow>>> ListAsync(ItemId item, DateRange range, IReadOnlySet<FlowKind>? kinds, CancellationToken ct);

    /// <summary>Exact sum in the item's currency; zero in that currency when nothing matches.</summary>
    Task<Result<Money>> SumAsync(ItemId item, IReadOnlySet<FlowKind> kinds, DateRange range, CancellationToken ct);
}

/// <summary><c>core-data-model.md</c> §2: "today" is the household's calendar day, in <c>household_settings.timezone</c>.</summary>
public interface IHouseholdCalendar
{
    Task<LocalDate> TodayAsync(CancellationToken ct);
}

/// <summary>2A spec §5.</summary>
public static class CoreFactCodes
{
    public const string ItemWithoutCurrency = "core.item_without_currency";
    public const string CurrencyMismatch = "core.currency_mismatch";
    public const string DateInFuture = "core.date_in_future";
    public const string AmountNotPositive = "core.amount_not_positive";
    public const string InvalidFact = "core.invalid_fact";
    public const string FactNotFound = "core.fact_not_found";
    public const string FactRetired = "core.fact_retired";
    public const string OpeningExists = "core.opening_exists";
    public const string ExternalRefTaken = "core.external_ref_taken";
    public const string FactConflict = "core.fact_conflict";
}
