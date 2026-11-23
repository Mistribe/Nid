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
        jobs.MapGet("", async (string? status, string? type, int? page, IInvoker invoker, CancellationToken ct) =>
            {
                if (status is not null && !ModelJobPage.Statuses.Contains(status))
                {
                    var reader = new FieldReader();
                    reader.Add("status", "invalid", "queued, running, succeeded, failed or dead.");
                    return reader.Problem();
                }

                return (await invoker.InvokeAsync(new ListModelJobsQuery(status, string.IsNullOrEmpty(type) ? null : type, page), ct)).ToHttp(p => p);
            })
            .Returns<ModelJobPage>();
        jobs.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetModelJobQuery(id), ct)).ToHttp(view => view))
            .Returns<ModelJobView>();

        api.MapPut("/{id:guid}/safe", async (Guid id, SafeBody body, IInvoker invoker, CancellationToken ct) =>
                body.Safe is { } safe
                    ? (await invoker.InvokeAsync(new SetWorkerSafeCommand(id, safe), ct)).ToHttp(change => change.View)
                    : Missing("safe"))
            .Returns<WorkerView>();

        routes.MapGroup("/api/v1/ai/capabilities").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            .MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetModelCapabilitiesQuery(), ct)).ToHttp(view => view))
            .Returns<ModelCapabilities>();

        var settings = routes.MapGroup("/api/v1/ai/settings").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        settings.MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetAiSettingsQuery(), ct)).ToHttp(view => view))
            .Returns<AiSettingsView>();
        settings.MapPut("/remote-workers", async (RemoteWorkersBody body, IInvoker invoker, CancellationToken ct) =>
                body.Allowed is { } allowed
                    ? (await invoker.InvokeAsync(new SetRemoteWorkersAllowedCommand(allowed), ct)).ToHttp(change => change.View)
                    : Missing("allowed"))
            .Returns<AiSettingsView>();

        settings.MapPut("/uses/{use}", async (string use, UseModeBody body, IInvoker invoker, CancellationToken ct) =>
                body.Mode is { } mode
                    ? (await invoker.InvokeAsync(new SetUseModeCommand(use, mode), ct)).ToHttp(change => change.View)
                    : Missing("mode"))
            .Returns<AiSettingsView>();
        settings.MapPut("/uses/{use}/model", async (string use, UseModelBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SetUseModelCommand(use, string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim()), ct)).ToHttp(view => view))
            .Returns<AiSettingsView>();

        routes.MapGroup("/api/v1/ai/models").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            .MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListDeclaredModelsQuery(), ct)).ToHttp(models => models))
            .Returns<DeclaredModels>();

        // #487: the cloud profiles, the region, the providers' keys.
        var cloud = routes.MapGroup("/api/v1/ai/cloud").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        cloud.MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetCloudSettingsQuery(), ct)).ToHttp(view => view))
            .Returns<CloudSettingsView>();
        cloud.MapPut("/region", async (CloudRegionBody body, IInvoker invoker, CancellationToken ct) =>
                body.Region is { } region
                    ? (await invoker.InvokeAsync(new SetCloudRegionCommand(region), ct)).ToHttp(view => view)
                    : Missing("region"))
            .Returns<CloudSettingsView>();
        cloud.MapPost("/profiles/{id}/enable", async (string id, EnableProfileBody body, IInvoker invoker, CancellationToken ct) =>
                body.TermsReviewedOn is { } reviewed
                    ? (await invoker.InvokeAsync(new EnableCloudProfileCommand(id, reviewed), ct)).ToHttp(view => view)
                    : Missing("termsReviewedOn"))
            .Returns<CloudSettingsView>();
        cloud.MapPost("/profiles/{id}/disable", async (string id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new DisableCloudProfileCommand(id), ct)).ToHttp(view => view))
            .Returns<CloudSettingsView>();
        cloud.MapPut("/keys/{provider}", async (string provider, ProviderKeyBody body, IInvoker invoker, CancellationToken ct) =>
                body.Key is { Length: > 0 } secret
                    ? (await invoker.InvokeAsync(new SetProviderKeyCommand(provider, secret), ct)).ToHttp(view => view)
                    : Missing("key"))
            .Returns<CloudSettingsView>();
        cloud.MapDelete("/keys/{provider}", async (string provider, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RevokeProviderKeyCommand(provider), ct)).ToHttp(view => view))
            .Returns<CloudSettingsView>();

        // #489: « Utilisation IA »: the reader's own calls and the household's month.
        routes.MapGroup("/api/v1/ai/usage").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>()
            .MapGet("", async (int? page, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetLlmUsageQuery(page), ct)).ToHttp(view => view))
            .Returns<LlmUsageView>();

        // #488: the household's monthly budget for paid providers.
        var budget = routes.MapGroup("/api/v1/ai/budget").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();
        budget.MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetBudgetQuery(), ct)).ToHttp(view => view))
            .Returns<AiBudgetView>();
        budget.MapPut("", async (AiBudgetBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var amount = reader.Decimal(body.MonthlyBudgetEur, "monthlyBudgetEur");
                return amount is { } eur
                    ? (await invoker.InvokeAsync(new SetMonthlyBudgetCommand(eur), ct)).ToHttp(view => view)
                    : body.MonthlyBudgetEur is null ? Missing("monthlyBudgetEur") : reader.Problem();
            })
            .Returns<AiBudgetView>();

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

    public sealed record SafeBody(bool? Safe);

    public sealed record RemoteWorkersBody(bool? Allowed);

    public sealed record UseModeBody(string? Mode);

    public sealed record UseModelBody(string? Model);

    public sealed record CloudRegionBody(string? Region);

    /// <summary>A decimal string in euros, such as <c>"12.50"</c>.</summary>
    public sealed record AiBudgetBody(string? MonthlyBudgetEur);

    public sealed record EnableProfileBody(NodaTime.LocalDate? TermsReviewedOn);

    /// <summary>The key is read once, stored encrypted, and never sent back.</summary>
    public sealed record ProviderKeyBody(string? Key);

    private static IResult Missing(string field)
    {
        var reader = new FieldReader();
        reader.Add(field, "required", "Required.");
        return reader.Problem();
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
