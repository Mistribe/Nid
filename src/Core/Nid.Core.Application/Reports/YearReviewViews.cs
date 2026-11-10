using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Reports;

/// <summary>
/// Issue #12: a part of the net worth's change, under the key <see cref="MonthChanges.NetWorthParts"/> gives it. <c>Low</c> and
/// <c>High</c> are its bar: the running total from the start before and after it, in the order of the list.
/// </summary>
public sealed record ReviewPartView(string Key, DashboardMoneyView Amount, DashboardMoneyView Low, DashboardMoneyView High);

/// <summary>Issue #12: the net worth on <c>Start</c> and <c>End</c>, its change in parts, and the previous period's (null without one).</summary>
public sealed record NetWorthReviewView(
    DashboardMoneyView Start, DashboardMoneyView End, DashboardMoneyView Change, IReadOnlyList<ReviewPartView> Parts,
    DashboardMoneyView? PreviousChange, IReadOnlyList<ReviewPartView> PreviousParts);

/// <summary>Issue #12: one envelope of the year, in the household currency; <c>NoStartValue</c>: its start counted at the amount paid in.</summary>
public sealed record InvestmentsReviewRowView(
    ItemId Id, string Name, string Kind, DashboardMoneyView? StartValue, DashboardMoneyView? EndValue, DashboardMoneyView? NetContributions,
    DashboardMoneyView? Gain, decimal? PeriodReturnPct, bool StaleStart, bool NoStartValue);

/// <summary>Issue #12: the investments of the year as the Performance page counts them, and the previous period's gain and return.</summary>
public sealed record InvestmentsReviewView(
    DashboardMoneyView? StartValue, DashboardMoneyView? EndValue, DashboardMoneyView? Contributions, DashboardMoneyView? Withdrawals,
    DashboardMoneyView? Gain, decimal? PeriodReturnPct, DashboardMoneyView? PreviousGain, decimal? PreviousPeriodReturnPct,
    IReadOnlyList<InvestmentsReviewRowView> Envelopes, IReadOnlyList<string> Findings);

/// <summary>
/// Issue #12: the year from <c>Start</c> (31 December before it) to <c>End</c> (its 31 December, or today when <c>ToDate</c>);
/// <c>Years</c> are those the household can review. A section is null when it has nothing to show. <c>Findings</c> are the codes of
/// what a section could not convert (a missing rate, in this period or the previous one): the reason the net worth, or a figure, is absent.
/// </summary>
public sealed record YearReviewView(
    int Year, LocalDate Start, LocalDate End, bool ToDate, IReadOnlyList<int> Years,
    NetWorthReviewView? NetWorth, InvestmentsReviewView? Investments, IncomeReviewView? Income,
    SubscriptionsReviewView? Subscriptions, LoansReviewView? Loans, PropertiesReviewView? Properties, IReadOnlyList<string> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => (Investments?.Envelopes.Select(e => e.Id) ?? []).Concat(Income?.Sources.Where(s => s.Id is not null).Select(s => s.Id!.Value) ?? [])
            .Concat(Subscriptions is { } s ? s.Started.Select(i => i.Id).Concat(s.Ended.Select(i => i.Id)).Concat(s.PriceChanges.Select(c => c.Id)) : [])
            .Concat(Loans?.Loans.Select(l => l.Id) ?? []).Concat(Properties?.Properties.Select(p => p.Id) ?? []);
}

/// <summary>Issue #12: the year's subscriptions: the cost of its charges, those started, ended and repriced, and the previous period's cost.</summary>
public sealed record SubscriptionsReviewView(
    DashboardMoneyView? Cost, DashboardMoneyView? PreviousCost, IReadOnlyList<SubscriptionReviewItemView> Started,
    IReadOnlyList<SubscriptionReviewItemView> Ended, IReadOnlyList<SubscriptionPriceChangeView> PriceChanges);

/// <summary>Issue #12: a subscription that started (its first charge) or ended (the coverage end the subscription page shows, <c>Timeline.ExpiresOn</c>) on <c>On</c>.</summary>
public sealed record SubscriptionReviewItemView(ItemId Id, string Name, string? Provider, LocalDate On);

/// <summary>Issue #12: a charging cycle, <c>Unit</c> one of day, week, month, year.</summary>
public sealed record ReviewDurationView(int Count, string Unit)
{
    public static ReviewDurationView From(Nid.Core.Contracts.Duration d) => new(d.Count, d.Unit.ToString().ToLowerInvariant());
}

/// <summary>Issue #12: a new price or cycle from <c>On</c>, in the subscription's currency; <c>PromoEnded</c>: the earlier one had a cycle limit.</summary>
public sealed record SubscriptionPriceChangeView(
    ItemId Id, string Name, LocalDate On, DashboardMoneyView Before, ReviewDurationView BeforeEvery, DashboardMoneyView After,
    ReviewDurationView AfterEvery, bool PromoEnded);

/// <summary>Issue #12: the year's loans as their schedules count them, and the previous period's cost of credit and capital repaid.</summary>
public sealed record LoansReviewView(
    DashboardMoneyView? Interest, DashboardMoneyView? Insurance, DashboardMoneyView? Fees, DashboardMoneyView? CapitalRepaid,
    DashboardMoneyView? StartBalance, DashboardMoneyView? EndBalance, DashboardMoneyView? PreviousInterestAndInsurance,
    DashboardMoneyView? PreviousCapitalRepaid, IReadOnlyList<LoanReviewRowView> Loans);

/// <summary>Issue #12: a loan of the year; <c>ArchivedOn</c>: archived in it, its end balance then 0.</summary>
public sealed record LoanReviewRowView(
    ItemId Id, string Name, DashboardMoneyView? Interest, DashboardMoneyView? Insurance, DashboardMoneyView? Fees, DashboardMoneyView? CapitalRepaid,
    DashboardMoneyView? StartBalance, DashboardMoneyView? EndBalance, LocalDate? ArchivedOn);

/// <summary>Issue #12: the year's property costs and rents, and the previous period's.</summary>
public sealed record PropertiesReviewView(
    DashboardMoneyView? Costs, DashboardMoneyView? Rents, DashboardMoneyView? PreviousCosts, DashboardMoneyView? PreviousRents,
    IReadOnlyList<PropertyReviewRowView> Properties);

/// <summary>Issue #12: a property of the year: its costs by category, largest first, its rents and its yields over the year (null: not let before it).</summary>
public sealed record PropertyReviewRowView(
    ItemId Id, string Name, DashboardMoneyView? Costs, IReadOnlyList<PropertyCostCategoryView> ByCategory, DashboardMoneyView? Rents,
    decimal? GrossYieldPct, decimal? NetYieldPct);

/// <summary>Issue #12: an expense category (<c>other</c> without one) and what it paid in the year.</summary>
public sealed record PropertyCostCategoryView(string Category, DashboardMoneyView? Amount);

/// <summary>
/// Issue #12: the year's income as the income page counts it, its engagements (each month's cash due, and the tax estimate's months) and
/// the margin between them, beside the previous period's (null without one). An amount is null when a rate is missing.
/// </summary>
public sealed record IncomeReviewView(
    DashboardMoneyView? Income, IReadOnlyList<IncomeSourceView> Sources,
    DashboardMoneyView? Engagements, IReadOnlyList<EngagementSourceView> EngagementSources,
    DashboardMoneyView? Margin, DashboardMoneyView? PreviousIncome, DashboardMoneyView? PreviousEngagements, DashboardMoneyView? PreviousMargin);

/// <summary>Issue #12: one source of the year's income; <c>Key</c> is its item id, or <c>one_off</c> for the one-offs together.</summary>
public sealed record IncomeSourceView(ItemId? Id, string Key, string? Name, string? Kind, DashboardMoneyView? Amount);

/// <summary>Issue #12: one source of the year's engagements, one of <see cref="CommitmentSources"/>; <c>Estimated</c>: not yet known.</summary>
public sealed record EngagementSourceView(string Source, DashboardMoneyView? Amount, bool Estimated);
