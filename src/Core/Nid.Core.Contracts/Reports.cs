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

/// <summary>Issue #12: one envelope of the period, in the reference currency.</summary>
public sealed record InvestmentsReviewRow(
    ItemId Id, string Name, string Kind, decimal? StartValue, decimal? EndValue, decimal? NetContributions, decimal? Gain,
    decimal? PeriodReturnPct, bool StaleStart);
