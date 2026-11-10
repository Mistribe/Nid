using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>Issue #12: the yearly review's investments, from the same period rule as the Performance page.</summary>
public sealed class InvestmentsReview(InvestmentsPeriodReader period) : IInvestmentsReview
{
    public async Task<Result<InvestmentsReviewPart>> ReviewAsync(
        PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
        => (await period.ReadAsync(reader, scope, start, end, ct)).Map(f => f.ToReviewPart());
}
