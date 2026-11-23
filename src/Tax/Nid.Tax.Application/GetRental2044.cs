using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Rental 2044 spec §5: the form of a revenue year, last year's by default (the one declared).</summary>
public sealed record GetRental2044Query(int? Year, VisibilityScope Scope) : IQuery<Rental2044View>;

[RequestHandler<GetRental2044Query, Rental2044View>]
public sealed class GetRental2044Handler(TaxEstimates estimates, IHouseholdCalendar calendar) : IRequestHandler<GetRental2044Query, Rental2044View>
{
    public async ValueTask<Result<Rental2044View>> HandleAsync(GetRental2044Query query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var year = query.Year ?? today.Year - 1;
        return TaxEstimates.CheckYear(year, today) is { } failure
            ? Result.Failure<Rental2044View>(failure)
            : await estimates.Rental2044Async(year, query.Scope, today, ct);
    }
}
