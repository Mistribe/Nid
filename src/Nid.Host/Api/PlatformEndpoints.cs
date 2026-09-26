using Nid.Core.Contracts;
using Nid.Platform;
using Nid.Platform.Audit;
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

        api.MapGet("/audit/items/{id:guid}", async (Guid id, long? before, int? limit, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ItemHistoryQuery(new ItemId(id), before, limit), ct)).ToHttp(p => p))
            .Returns<ItemHistoryPage>();

        api.MapGet("/audit/me/security", async (long? before, int? limit, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new MySecurityEventsQuery(before, limit), ct)).ToHttp(p => p))
            .Returns<SecurityEventPage>();

        return routes;
    }
}
