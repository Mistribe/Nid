using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>The household's opt-in for remote workers (<c>docs/ai/README.md</c> §6.2, §12): any adult decides it, in the
/// browser; it is kept as a consent and every adult is told.</summary>
public sealed record SetRemoteWorkersAllowedCommand(bool Allowed) : ICommand<AiSettingsView>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.AiRemoteWorkersChanged, null, [], new Dictionary<string, string> { ["allowed"] = Allowed ? "true" : "false" });
}

/// <summary>Marks a worker as trusted with local-only data (<c>safe</c>), whatever its placement: any adult, in the browser.</summary>
public sealed record SetWorkerSafeCommand(Guid Id, bool Safe) : ICommand<WorkerView>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is WorkerView view ? WorkerEvents.Of(SecurityEventNames.ModelWorkerChanged, view.Id, view.Name, Safe ? "safe_on" : "safe_off") : null;
}

public sealed record GetAiSettingsQuery : IQuery<AiSettingsView>;

internal static class DataPolicyNotices
{
    public const string Kind = "ai.data_policy_changed";

    /// <summary>Every adult is told of a change to what may leave the premises or reach which worker (§12).</summary>
    public static Task TellAsync(INotifier notifier, string change, string? worker, Instant at, CancellationToken ct)
    {
        var data = new Dictionary<string, string> { ["change"] = change };
        if (worker is not null)
            data["worker"] = worker;
        return notifier.PublishAsync(new Notice(Kind, new Recipients.AllAdults(), $"{Kind}:{change}:{at.ToUnixTimeTicks()}", [], data), ct);
    }
}

[RequestHandler<SetRemoteWorkersAllowedCommand, AiSettingsView>]
public sealed class SetRemoteWorkersAllowedHandler(IAiSettings settings, IActorAccessor actors, INotifier notifier, IClock clock)
    : IRequestHandler<SetRemoteWorkersAllowedCommand, AiSettingsView>
{
    public async ValueTask<Result<AiSettingsView>> HandleAsync(SetRemoteWorkersAllowedCommand command, CancellationToken ct = default)
    {
        if (actors.Current?.PersonId is not { } person)
            return Result.Failure<AiSettingsView>(new UnauthorizedFailure("An adult decides the household's opt-in.", null));

        var now = clock.GetCurrentInstant();
        await settings.SetRemoteWorkersAllowedAsync(command.Allowed, person.Value, now, ct);
        await DataPolicyNotices.TellAsync(notifier, command.Allowed ? "remote_on" : "remote_off", null, now, ct);
        return Result.Success(new AiSettingsView(command.Allowed));
    }
}

[RequestHandler<SetWorkerSafeCommand, WorkerView>]
public sealed class SetWorkerSafeHandler(IWorkerStore store, INidKeys keys, IWorkerDirectory directory, INotifier notifier, IClock clock)
    : IRequestHandler<SetWorkerSafeCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(SetWorkerSafeCommand command, CancellationToken ct = default)
    {
        var result = await WorkerManagement.ChangeAsync(command.Id, store, keys, directory, clock, ct,
            w => WorkerRules.Revoke(w.Status), w => w with { Safe = command.Safe });
        if (result.TryGetValue(out var view))
            await DataPolicyNotices.TellAsync(notifier, command.Safe ? "safe_on" : "safe_off", view.Name, clock.GetCurrentInstant(), ct);
        return result;
    }
}

[RequestHandler<GetAiSettingsQuery, AiSettingsView>]
public sealed class GetAiSettingsHandler(IAiSettings settings) : IRequestHandler<GetAiSettingsQuery, AiSettingsView>
{
    public async ValueTask<Result<AiSettingsView>> HandleAsync(GetAiSettingsQuery query, CancellationToken ct = default)
        => Result.Success(new AiSettingsView(await settings.RemoteWorkersAllowedAsync(ct)));
}
