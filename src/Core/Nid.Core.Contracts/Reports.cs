using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public static class ReportCodes
{
    /// <summary>Issue #12: a year after the current one, or before the first with a holding.</summary>
    public const string InvalidYear = "reports.invalid_year";
}

/// <summary>Issue #12: the investments over [Start, End], converted into the reference currency as the Performance page does.</summary>
public interface IInvestmentsReview
{
    Task<Result<InvestmentsReviewPart>> ReviewAsync(PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct);
}

/// <summary>Issue #12: the investments' figures of a period; <c>Findings</c> are the codes of what could not be converted (a missing rate).</summary>
public sealed record InvestmentsReviewPart(
    decimal? StartValue, decimal? EndValue, decimal? Contributions, decimal? Withdrawals, decimal? Gain, decimal? PeriodReturnPct,
    IReadOnlyList<InvestmentsReviewRow> Envelopes, IReadOnlyList<string> Findings);

/// <summary>Issue #12: one envelope of the period, in the reference currency; <c>NoStartValue</c>: its start counted at the amount paid in.</summary>
public sealed record InvestmentsReviewRow(
    ItemId Id, string Name, string Kind, decimal? StartValue, decimal? EndValue, decimal? NetContributions, decimal? Gain,
    decimal? PeriodReturnPct, bool StaleStart, bool NoStartValue);

public interface IIncomeReview
{
    /// <summary>Issue #12: the income due in (start, end], in the reference currency at each due's date, as the income overview counts it.</summary>
    Task<Result<IncomeReviewPart>> ReviewAsync(PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct);

    /// <summary>
    /// Issue #12: the earliest due of the visible sources, archived ones included (a phase's first occurrence, a one-off or a rent), up to
    /// today and no further back than <see cref="MaxYears"/> years; null without any. The years the review offers start no later.
    /// </summary>
    Task<LocalDate?> FirstDueAsync(PersonId reader, VisibilityScope scope, CancellationToken ct);

    /// <summary>How far back the annual review reaches.</summary>
    const int MaxYears = 50;
}

/// <summary>Issue #12: the period's income; <c>Total</c> is null when a due could not be converted (<c>MissingCurrencies</c>).</summary>
public sealed record IncomeReviewPart(decimal? Total, IReadOnlyList<IncomeReviewSource> Sources, IReadOnlyList<string> MissingCurrencies);

/// <summary>
/// Issue #12: one source of the period, largest first. <c>Key</c> is the item id, or <c>one_off</c> for every one-off together
/// (no id, name nor kind); <c>Kind</c> is the source kind's wire value, <c>rent</c> for a rent.
/// </summary>
public sealed record IncomeReviewSource(ItemId? Id, string Key, string? Name, string? Kind, decimal? Amount);

public interface ISubscriptionsReview
{
    /// <summary>
    /// Issue #12: the subscriptions of (start, end], every visible one archived included for its charges before its archive day.
    /// The cost converts each charge at the rate of its date, as <c>GET /subscriptions?from=&amp;to=</c> does.
    /// </summary>
    Task<Result<SubscriptionsReviewPart>> ReviewAsync(PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct);

    /// <summary>Issue #12: the earliest charge made by today of the visible subscriptions, archived ones included; null without any.</summary>
    Task<LocalDate?> FirstChargeAsync(PersonId reader, VisibilityScope scope, CancellationToken ct);
}

/// <summary>Issue #12: the period's subscriptions; <c>Cost</c> is null when a charge could not be converted (<c>MissingCurrencies</c>).</summary>
public sealed record SubscriptionsReviewPart(
    decimal? Cost, IReadOnlyList<SubscriptionReviewItem> Started, IReadOnlyList<SubscriptionReviewItem> Ended,
    IReadOnlyList<SubscriptionPriceChange> PriceChanges, IReadOnlyList<string> MissingCurrencies);

/// <summary>Issue #12: a subscription that started (its first charge) or ended (its last day covered, without successor) on <c>On</c>.</summary>
public sealed record SubscriptionReviewItem(ItemId Id, string Name, string? Provider, LocalDate On);

public interface ILoansReview
{
    /// <summary>
    /// Issue #12: the loans of (start, end], every visible one archived and ended ones included, as <c>LoanMath.At(end) − LoanMath.At(start)</c>;
    /// an archived loan read on its last counted day. The period sums convert at <paramref name="end"/>'s rate, the start balance at
    /// <paramref name="start"/>'s, the end balance at <paramref name="end"/>'s.
    /// </summary>
    Task<Result<LoansReviewPart>> ReviewAsync(PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct);
}

/// <summary>Issue #12: the period's loans, in the reference currency; a total is null when a loan's figure could not be converted (<c>MissingCurrencies</c>).</summary>
public sealed record LoansReviewPart(
    decimal? Interest, decimal? Insurance, decimal? Fees, decimal? CapitalRepaid, decimal? StartBalance, decimal? EndBalance,
    IReadOnlyList<LoanReviewRow> Loans, IReadOnlyList<string> MissingCurrencies);

/// <summary>
/// Issue #12: a loan with activity or a balance in the period; <c>CapitalRepaid</c>: its scheduled principal and early repayments.
/// <c>ArchivedOn</c>: its archive day when it falls in the period; it then leaves the books, its <c>EndBalance</c> 0.
/// </summary>
public sealed record LoanReviewRow(ItemId Id, string Name, decimal? Interest, decimal? Insurance, decimal? Fees, decimal? CapitalRepaid,
    decimal? StartBalance, decimal? EndBalance, LocalDate? ArchivedOn);

/// <summary>Issue #12: a phase following another at a different price or cycle; <c>PromoEnded</c>: the earlier one had a cycle limit.</summary>
public sealed record SubscriptionPriceChange(ItemId Id, string Name, LocalDate On, decimal BeforeAmount, string BeforeCurrency, Duration BeforeEvery,
    decimal AfterAmount, string AfterCurrency, Duration AfterEvery, bool PromoEnded);

public interface IPropertiesReview
{
    /// <summary>
    /// Issue #12: the properties of (start, end], every visible one, archived ones for their flows before their archive day. The costs
    /// and rents convert at <paramref name="end"/>'s rate; the yields are the period's, not annualised.
    /// </summary>
    Task<Result<PropertiesReviewPart>> ReviewAsync(PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct);
}

/// <summary>Issue #12: the period's properties, in the reference currency; a total is null when a figure could not be converted (<c>MissingCurrencies</c>).</summary>
public sealed record PropertiesReviewPart(decimal? Costs, decimal? Rents, IReadOnlyList<PropertyReviewRow> Properties, IReadOnlyList<string> MissingCurrencies);

/// <summary>
/// Issue #12: a property held in the period: its expenses paid by category, largest first, its rents received (rent and recoverable
/// charges), and its gross and net yields in percent over the period, null when it was not let before the period's first day.
/// </summary>
public sealed record PropertyReviewRow(ItemId Id, string Name, decimal? Costs, IReadOnlyList<PropertyCostCategory> ByCategory, decimal? Rents,
    decimal? GrossYieldPct, decimal? NetYieldPct);

/// <summary>Issue #12: an expense category (README §4, <c>other</c> without one) and what it paid in the period.</summary>
public sealed record PropertyCostCategory(string Category, decimal? Amount);
