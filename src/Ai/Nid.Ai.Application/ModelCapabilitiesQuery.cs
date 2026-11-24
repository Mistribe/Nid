using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
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
            .Select(w => (w.Worker, Declaration: w.Declaration!, Online: WorkerRouting.Online(w.Worker.LastSeenAt, now)))
            .ToList();

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, declaration, _) in workers)
            foreach (var model in declaration.Models)
                names.Add(Name(model.Capability));

        var views = new List<CapabilityView> { View(Echo, workers.Where(w => Serves(w.Declaration, Echo)).ToList(), remoteAllowed) };
        views.AddRange(names.Select(name => View(name, workers.Where(w => w.Declaration.Models.Any(m => Name(m.Capability) == name)).ToList(), remoteAllowed)));

        // #486: the chat is always answered for, and says first what the household's settings stop.
        if (views.All(v => v.Capability != ModelUses.Chat))
            views.Add(View(ModelUses.Chat, [], remoteAllowed));
        var chat = await settings.UseAsync(ModelUses.Chat, ct);
        var index = views.FindIndex(v => v.Capability == ModelUses.Chat);
        views[index] = views[index] with
        {
            Reason = chat.Mode == UseModes.Off ? JobCodes.UseOff
                : views[index].Reason is null && chat.Model is null ? JobCodes.NoModel
                : views[index].Reason is null && !workers.Any(w => w.Online && w.Declaration.Models.Any(m => m.Name == chat.Model)) ? JobCodes.ModelUnavailable
                : views[index].Reason,
        };
        return Result.Success(new ModelCapabilities(remoteAllowed, views));
    }

    private static CapabilityView View(string name, IReadOnlyList<(WorkerRecord Worker, WorkerDeclaration Declaration, bool Online)> serving, bool remoteAllowed)
    {
        var allowed = serving.Where(s => s.Worker.Placement == WorkerPlacement.Local || remoteAllowed).ToList();
        var reachable = allowed.Where(s => s.Online).Select(s => s.Worker).ToList();
        var reason = reachable.Count > 0 ? null
            : serving.Count == 0 ? JobCodes.NoWorker
            : allowed.Count == 0 ? Domain.DataPolicy.RemoteNotAllowed
            : JobCodes.AllOffline;
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
