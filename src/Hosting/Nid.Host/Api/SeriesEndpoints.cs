using Nid.Core.Application.Series;
using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary><c>/api/v1/series</c>: the series explorer's catalogue and the points of a selection, for any adult.</summary>
public static class SeriesEndpoints
{
    public static IEndpointRouteBuilder MapSeriesEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/series").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? q, string? kinds, int? page, string? scope, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new ListSeriesQuery(r.Scope(scope), q, page ?? 1, SeriesKeys.ParseKinds(kinds)), view => view, ct))
            .Returns<SeriesCatalogView>();

        api.MapGet("/points", async (string? s, string? from, string? to, string? scope, IInvoker invoker, CancellationToken ct)
            => await FieldReader.Run(invoker, r => new GetSeriesPointsQuery(r.Scope(scope), s,
                r.OptionalDate(from, "from"), r.OptionalDate(to, "to")), view => view, ct))
            .Returns<SeriesPointsView>();

        return routes;
    }
}
