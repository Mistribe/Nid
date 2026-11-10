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
}

/// <summary>Issue #12: the period's income; <c>Total</c> is null when a due could not be converted (<c>MissingCurrencies</c>).</summary>
public sealed record IncomeReviewPart(decimal? Total, IReadOnlyList<IncomeReviewSource> Sources, IReadOnlyList<string> MissingCurrencies);

/// <summary>
/// Issue #12: one source of the period, largest first. <c>Key</c> is the item id, or <c>one_off</c> for every one-off together
/// (no id, name nor kind); <c>Kind</c> is the source kind's wire value, <c>rent</c> for a rent.
/// </summary>
public sealed record IncomeReviewSource(ItemId? Id, string Key, string? Name, string? Kind, decimal? Amount);
