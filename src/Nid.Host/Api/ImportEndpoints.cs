using Nid.Core.Contracts;
using Nid.Platform.Import;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/platform/imports").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapPost("", async (StartImportBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(), ct)).ToHttp(v => v, StatusCodes.Status201Created));

        api.MapGet("", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListImportsQuery(), ct)).ToHttp(v => v));

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetImportQuery(id), ct)).ToHttp(v => v));

        api.MapDelete("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CancelImportCommand(id), ct)).ToHttp(v => v));

        return routes;
    }

    public sealed record StartImportBody(string Format, int Version, IReadOnlyDictionary<string, Guid>? Adults)
    {
        public StartImportCommand ToCommand() => new(
            Format, Version, (Adults ?? new Dictionary<string, Guid>()).ToDictionary(a => a.Key, a => new PersonId(a.Value)));
    }
}
