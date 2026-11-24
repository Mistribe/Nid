using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Issue #307: the household's tax year by year, up to this year.</summary>
public sealed record GetTaxHistoryQuery(VisibilityScope Scope) : IQuery<TaxHistoryView>;

[RequestHandler<GetTaxHistoryQuery, TaxHistoryView>]
public sealed class GetTaxHistoryHandler(TaxEstimates estimates, IHouseholdCalendar calendar) : IRequestHandler<GetTaxHistoryQuery, TaxHistoryView>
{
    public async ValueTask<Result<TaxHistoryView>> HandleAsync(GetTaxHistoryQuery query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        return (await estimates.HistoryAsync(today.Year, query.Scope, today, ct)).Map(years => new TaxHistoryView(years));
    }
}
