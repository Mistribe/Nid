using Nid.Ai.Application;
using Nid.Ai.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary>The model workers (<c>docs/ai/README.md</c> §3, §12): any adult reads the list; an admin registers, pairs and
/// manages them, in the browser only.</summary>
public static class AiEndpoints
{
    public static void MapAiEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/ai/workers").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListWorkersQuery(), ct)).ToHttp(list => list))
            .Returns<WorkerList>();

        api.MapPost("", async (RegisterWorkerBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(registered => registered, StatusCodes.Status201Created);
            })
            .Returns<RegisteredWorker>(StatusCodes.Status201Created);

        api.MapPost("/{id:guid}/pair", async (Guid id, PairWorkerBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var fingerprint = reader.Text(body.Fingerprint, "fingerprint");
                var code = reader.Text(body.Code, "code");
                return reader.Ok
                    ? (await invoker.InvokeAsync(new PairWorkerCommand(id, fingerprint!, code!), ct)).ToHttp(view => view)
                    : reader.Problem();
            })
            .Returns<WorkerView>();
    }

    public sealed record RegisterWorkerBody(string? Name, string? Url, string? Placement, bool? ProxyDeclared)
    {
        internal RegisterWorkerCommand? ToCommand(FieldReader reader)
        {
            var name = reader.Text(Name, "name");
            var url = reader.Text(Url, "url");
            WorkerPlacement? placement = Placement switch
            {
                "local" => WorkerPlacement.Local,
                "remote" => WorkerPlacement.Remote,
                _ => null,
            };
            if (placement is null)
                reader.Add("placement", "invalid", "local or remote.");
            return reader.Ok ? new RegisterWorkerCommand(name!, url!, placement!.Value, ProxyDeclared ?? false) : null;
        }
    }

    public sealed record PairWorkerBody(string? Fingerprint, string? Code);
}
