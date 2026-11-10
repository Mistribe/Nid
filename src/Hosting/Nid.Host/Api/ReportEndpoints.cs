using Nid.Core.Application.Reports;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class ReportEndpoints
{
    /// <summary>Issue #12: the annual review of the net worth and the investments.</summary>
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/reports").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/year", async (int? year, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetYearReviewQuery(year, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<YearReviewView>();

        return routes;
    }
}
