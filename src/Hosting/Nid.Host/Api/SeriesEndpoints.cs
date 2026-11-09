using Nid.Core.Application.Series;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary><c>/api/v1/series</c>: the series explorer's catalogue and the points of a selection, for any adult.</summary>
public static class SeriesEndpoints
{
    public static IEndpointRouteBuilder MapSeriesEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/series").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? q, int? page, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListSeriesQuery(SubscriptionEndpoints.ParseScope(scope), q, page ?? 1), ct)).ToHttp(r => r))
            .Returns<SeriesCatalogView>();

        api.MapGet("/points", async (string? s, string? from, string? to, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetSeriesPointsQuery(SubscriptionEndpoints.ParseScope(scope), s,
                SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to)), ct)).ToHttp(r => r))
            .Returns<SeriesPointsView>();

        return routes;
    }
}
