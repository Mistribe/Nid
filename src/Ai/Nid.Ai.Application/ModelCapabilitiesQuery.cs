using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>Decision D: what model-backed features can run now, for any adult (no worker detail beyond counts).</summary>
public sealed record GetModelCapabilitiesQuery : IQuery<ModelCapabilities>;

[RequestHandler<GetModelCapabilitiesQuery, ModelCapabilities>]
public sealed class GetModelCapabilitiesHandler(IWorkerStore store, IAiSettings settings, IClock clock)
    : IRequestHandler<GetModelCapabilitiesQuery, ModelCapabilities>
{
    public const string Echo = "echo";

    public async ValueTask<Result<ModelCapabilities>> HandleAsync(GetModelCapabilitiesQuery query, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var remoteAllowed = await settings.RemoteWorkersAllowedAsync(ct);
        var workers = (await store.ListAsync(ct))
            .Where(w => w.Status == WorkerStatus.Active)
            .Select(w => (Worker: w, Declaration: WorkerRouter.DeclarationOf(w)))
            .Where(w => w.Declaration is not null)
            .Select(w => (w.Worker, Declaration: w.Declaration!, Online: w.Worker.LastSeenAt is { } seen && now - seen <= WorkerRouter.OnlineFor))
            .ToList();

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, declaration, _) in workers)
            foreach (var model in declaration.Models)
                names.Add(Name(model.Capability));

        var views = new List<CapabilityView> { View(Echo, workers.Where(w => Serves(w.Declaration, Echo)).ToList(), remoteAllowed) };
        views.AddRange(names.Select(name => View(name, workers.Where(w => w.Declaration.Models.Any(m => Name(m.Capability) == name)).ToList(), remoteAllowed)));
        return Result.Success(new ModelCapabilities(remoteAllowed, views));
    }

    private static CapabilityView View(string name, IReadOnlyList<(WorkerRecord Worker, WorkerDeclaration Declaration, bool Online)> serving, bool remoteAllowed)
    {
        var online = serving.Where(s => s.Online).Select(s => s.Worker).ToList();
        var reachable = online.Where(w => w.Placement == WorkerPlacement.Local || remoteAllowed).ToList();
        var reason = reachable.Count > 0 ? null
            : serving.Count == 0 ? "no_worker"
            : online.Count == 0 ? "all_offline"
            : "remote_not_allowed";
        return new CapabilityView(name,
            reachable.Count(w => w.Placement == WorkerPlacement.Local),
            reachable.Count(w => w.Placement == WorkerPlacement.Remote),
            reachable.Count(w => w.Safe),
            reason);
    }

    private static bool Serves(WorkerDeclaration declaration, string type)
        => declaration.Jobs.Any(j => j.Type == type) || declaration.Requests.Any(r => r.Type == type);

    private static string Name(DeclaredCapability capability) => JsonNamingPolicy.KebabCaseLower.ConvertName(capability.ToString());
}
