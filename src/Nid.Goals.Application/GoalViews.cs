using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Goals.Application;

/// <summary>An amount on the wire, in the household's reference currency unless said otherwise.</summary>
public sealed record GoalMoneyView(string Amount, string Currency)
{
    /// <summary>A computed amount, rounded to the cent (spec §3).</summary>
    public static GoalMoneyView Of(decimal amount, Currency currency)
        => new(Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), currency.Code);

    /// <summary>An entered amount, exact, as <see cref="Money.AmountText"/> writes it.</summary>
    public static GoalMoneyView Exact(decimal amount, Currency currency) => new(amount.ToString(CultureInfo.InvariantCulture), currency.Code);
}

/// <summary>
/// Spec §3, §5: a goal and its computed view. The figures are null when there is no target to measure against (an emergency fund
/// without commitments). <c>Partial</c>: a linked envelope is hidden from the reader in the scope (D1), never named.
/// </summary>
public sealed record GoalSummary(
    Guid Id, string Name, string Kind, IReadOnlyList<ItemOwnerView> Owners, string Visibility, bool Closed, LocalDate? TargetDate,
    GoalMoneyView? Target, GoalMoneyView? Reached, decimal? ProgressPct, GoalMoneyView? Remaining, GoalMoneyView? MonthlyNeeded,
    GoalMoneyView? MonthlyPace, LocalDate? ProjectedOn, string? Status, bool Partial, IReadOnlyList<string> Flags);

public sealed record GoalList(IReadOnlyList<GoalSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => new ItemId(i.Id));
}

/// <summary>
/// An envelope the reader sees funding the goal: <c>Value</c> in the envelope's currency, <c>Contribution</c> its share of it in the
/// reference currency (0 for a closed envelope, null without a rate).
/// </summary>
public sealed record GoalFundingView(
    Guid EnvelopeId, string Name, int ShareBp, GoalMoneyView? Value, GoalMoneyView? Contribution, LocalDate? AsOf, bool Stale, bool Estimated,
    IReadOnlyList<string> Flags);

public sealed record GoalEntryView(LocalDate On, GoalMoneyView Amount);

/// <summary>D5: a source of an emergency fund's target, its amortized month converted; null with <c>MissingCurrency</c> when a rate is missing.</summary>
public sealed record GoalCommitmentView(string Source, int Count, GoalMoneyView? Monthly, string? MissingCurrency);

/// <summary>A goal in full: its envelopes (those the reader sees) or its entries, and for an emergency fund the sources of its target.</summary>
public sealed record GoalDetail(
    GoalSummary Goal, int? Months, string? Note, IReadOnlyList<GoalFundingView> Funding, IReadOnlyList<GoalEntryView> Entries,
    IReadOnlyList<GoalCommitmentView> Commitments, uint Version) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Funding.Select(f => new ItemId(f.EnvelopeId)).Prepend(new ItemId(Goal.Id));
}
