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

        api.MapPatch("/{id:guid}", async (Guid id, RenameWorkerBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var name = reader.Text(body.Name, "name");
                return reader.Ok
                    ? (await invoker.InvokeAsync(new RenameWorkerCommand(id, name!), ct)).ToHttp(view => view)
                    : reader.Problem();
            })
            .Returns<WorkerView>();

        MapAction(api, "disable", id => new DisableWorkerCommand(id));
        MapAction(api, "enable", id => new EnableWorkerCommand(id));
        MapAction(api, "revoke", id => new RevokeWorkerCommand(id));
        MapAction(api, "rotate-key", id => new RotateWorkerKeyCommand(id));
        MapAction(api, "refresh", id => new RefreshWorkerCommand(id));

        api.MapPost("/{id:guid}/test", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new TestWorkerCommand(id), ct)).ToHttp(result => result))
            .Returns<WorkerTestResult>();

        var jobs = routes.MapGroup("/api/v1/ai/jobs").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        jobs.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetModelJobQuery(id), ct)).ToHttp(view => view))
            .Returns<ModelJobView>();

        var key = routes.MapGroup("/api/v1/ai/key").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        key.MapPost("/rotate", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RotateNidKeyCommand(), ct)).ToHttp(rotation => rotation))
            .Returns<NidKeyRotation>();
    }

    private static void MapAction<TCommand>(RouteGroupBuilder api, string action, Func<Guid, TCommand> command)
        where TCommand : ICommand<WorkerView>
        => api.MapPost($"/{{id:guid}}/{action}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(command(id), ct)).ToHttp(view => view))
            .Returns<WorkerView>();

    public sealed record RenameWorkerBody(string? Name);

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
