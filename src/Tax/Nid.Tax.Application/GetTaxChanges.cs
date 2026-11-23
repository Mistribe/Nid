using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Issue #410: what changed since last year in the year's estimate, line by line (this year by default).</summary>
public sealed record GetTaxChangesQuery(int? Year, VisibilityScope Scope) : IQuery<TaxChangesView>;

[RequestHandler<GetTaxChangesQuery, TaxChangesView>]
public sealed class GetTaxChangesHandler(TaxEstimates estimates, ITaxAssessmentStore assessments, IPersonRegistry persons, IActorAccessor actor, IHouseholdCalendar calendar)
    : IRequestHandler<GetTaxChangesQuery, TaxChangesView>
{
    public async ValueTask<Result<TaxChangesView>> HandleAsync(GetTaxChangesQuery query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var year = query.Year ?? today.Year;
        if (TaxEstimates.CheckYear(year, today) is { } failure)
            return Result.Failure<TaxChangesView>(failure);
        var now = await estimates.ComputeAsync(year, query.Scope, today, ct);
        if (now.TryGetFailure(out failure)) return Result.Failure<TaxChangesView>(failure!);
        TaxResult? before = null;
        if (year - 1 >= TaxRules.FirstYear)
        {
            var previous = await estimates.ComputeAsync(year - 1, query.Scope, today, ct);
            if (previous.TryGetFailure(out failure)) return Result.Failure<TaxChangesView>(failure!);
            previous.TryGetValue(out before);
        }
        now.TryGetValue(out var result);
        var people = await persons.ListAsync(ct);
        // The notices as the estimate reads them (tax assessments spec §5): the reader's, in Foyer the household's.
        var notices = (await assessments.ListForAsync(actor.Current!.PersonId!.Value, year - 1, ct))
            .Where(n => query.Scope != VisibilityScope.Household || TaxAssessmentAccess.InHousehold(n.Assessment, people)).Select(n => n.Assessment);
        return Result.Success(TaxChangesView.Of(year, TaxChanges.Of(result!, before, notices, TaxEstimates.Adults(people).Select(a => a.Id.Value).ToList())));
    }
}
