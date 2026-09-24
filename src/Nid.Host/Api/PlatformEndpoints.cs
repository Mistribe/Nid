using Nid.Platform;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/platform").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/capabilities", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetCapabilitiesQuery(), ct)).ToHttp(c => c))
            .Returns<CapabilitiesView>();

        return routes;
    }
}
