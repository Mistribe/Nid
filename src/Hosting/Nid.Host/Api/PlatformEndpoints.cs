using System.Reflection;
using Nid.Core.Contracts;
using Nid.Platform;
using Nid.Platform.Audit;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class PlatformEndpoints
{
    /// <summary>The version CI builds with (-p:Version); the "+commit" suffix the SDK appends is dropped.</summary>
    public static readonly string Version = typeof(PlatformEndpoints).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    /// <summary>On every response, so an open tab learns the server moved on without asking (the SPA compares it to its build).</summary>
    public const string VersionHeader = "X-Nid-Version";

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/platform").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/capabilities", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetCapabilitiesQuery(), ct)).ToHttp(c => c))
            .Returns<CapabilitiesView>();

        api.MapGet("/version", () => new VersionView(Version))
            .Returns<VersionView>();

        api.MapGet("/audit/items/{id:guid}", async (Guid id, long? before, int? limit, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ItemHistoryQuery(new ItemId(id), before, limit), ct)).ToHttp(p => p))
            .Returns<ItemHistoryPage>();

        api.MapGet("/audit/me/security", async (long? before, int? limit, string? kind, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new MySecurityEventsQuery(before, limit, kind), ct)).ToHttp(p => p))
            .Returns<SecurityEventPage>();

        return routes;
    }
}

public sealed record VersionView(string Version);
