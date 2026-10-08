using Nid.Core.Application.Dashboard;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class DashboardEndpoints
{
    /// <summary>2D1 spec §4: one aggregated read for the home page.</summary>
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/dashboard", async (string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetDashboardQuery(ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<DashboardView>();

        return routes;
    }
}
