using Nid.Platform.Export;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/platform/export").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/manifest", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ExportManifestQuery(), ct)).ToHttp(v => v))
            .Returns<ExportManifestView>();

        api.MapGet("/{section}", async (string section, string? cursor, int? limit, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ExportPageQuery(section, cursor, limit), ct)).ToHttp(v => v))
            .Returns<ExportPageView>();

        return routes;
    }
}
