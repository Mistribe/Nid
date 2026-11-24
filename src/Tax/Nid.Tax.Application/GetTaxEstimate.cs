using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Tax estimate spec §8: the year's profile, the inputs read from the modules, the result and what was left out.</summary>
public sealed record GetTaxEstimateQuery(int? Year, VisibilityScope Scope) : IQuery<TaxEstimateView>;

[RequestHandler<GetTaxEstimateQuery, TaxEstimateView>]
public sealed class GetTaxEstimateHandler(TaxEstimates estimates, IHouseholdCalendar calendar) : IRequestHandler<GetTaxEstimateQuery, TaxEstimateView>
{
    public async ValueTask<Result<TaxEstimateView>> HandleAsync(GetTaxEstimateQuery query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var year = query.Year ?? today.Year;
        return TaxEstimates.CheckYear(year, today) is { } failure
            ? Result.Failure<TaxEstimateView>(failure)
            : await estimates.EstimateAsync(year, query.Scope, today, ct);
    }
}
