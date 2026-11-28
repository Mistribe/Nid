using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>The household's opt-in for remote workers (<c>docs/ai/README.md</c> §6.2, §12): an admin decides it, in the
/// browser (#585); it is kept as a consent and every adult is told.</summary>
public sealed record SetRemoteWorkersAllowedCommand(bool Allowed) : ICommand<PolicyChange<AiSettingsView>>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is PolicyChange<AiSettingsView> { Changed: true }
            ? new(SecurityEventNames.AiRemoteWorkersChanged, null, [], new Dictionary<string, string> { ["allowed"] = Allowed ? "true" : "false" })
            : null;
}

/// <summary>What a policy command answers, and whether it changed anything: a change that changes nothing records,
/// audits and tells nothing.</summary>
public sealed record PolicyChange<T>(T View, bool Changed) where T : notnull;

/// <summary>Marks a worker as trusted with local-only data (<c>safe</c>), whatever its placement: an admin, in the browser
/// (#585); every adult is told.</summary>
public sealed record SetWorkerSafeCommand(Guid Id, bool Safe) : ICommand<PolicyChange<WorkerView>>, IAdminOnly, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is PolicyChange<WorkerView> { Changed: true, View: var view }
            ? WorkerEvents.Of(SecurityEventNames.ModelWorkerChanged, view.Id, Safe ? "safe_on" : "safe_off")
            : null;
}

public sealed record GetAiSettingsQuery : IQuery<AiSettingsView>;

internal static class DataPolicyNotices
{
    public const string Kind = "ai.data_policy_changed";

    /// <summary>Every adult is told of a change to what may leave the premises or reach which worker (§12).</summary>
    public static Task TellAsync(
        INotifier notifier, string change, string? worker, Instant at, CancellationToken ct, IReadOnlyDictionary<string, string>? more = null)
    {
        var data = new Dictionary<string, string>(more ?? new Dictionary<string, string>()) { ["change"] = change };
        if (worker is not null)
            data["worker"] = worker;
        return notifier.PublishAsync(new Notice(Kind, new Recipients.AllAdults(), $"{Kind}:{change}:{at.ToUnixTimeTicks()}", [], data), ct);
    }
}

[RequestHandler<SetRemoteWorkersAllowedCommand, PolicyChange<AiSettingsView>>]
public sealed class SetRemoteWorkersAllowedHandler(IAiSettings settings, IActorAccessor actors, INotifier notifier, IClock clock)
    : IRequestHandler<SetRemoteWorkersAllowedCommand, PolicyChange<AiSettingsView>>
{
    public async ValueTask<Result<PolicyChange<AiSettingsView>>> HandleAsync(SetRemoteWorkersAllowedCommand command, CancellationToken ct = default)
    {
        if (actors.Current?.PersonId is not { } person)
            return Result.Failure<PolicyChange<AiSettingsView>>(new UnauthorizedFailure("An adult decides the household's opt-in.", null));

        if (await settings.RemoteWorkersAllowedAsync(ct) == command.Allowed)
            return Result.Success(new PolicyChange<AiSettingsView>(await AiSettingsViews.ReadAsync(settings, ct), Changed: false));

        var now = clock.GetCurrentInstant();
        if (!await settings.SetRemoteWorkersAllowedAsync(command.Allowed, person.Value, now, ct))
            return Result.Failure<PolicyChange<AiSettingsView>>(
                new ConflictFailure("Another adult changed it at the same moment: read it again.") { Code = JobCodes.SettingsChanged });
        await DataPolicyNotices.TellAsync(notifier, command.Allowed ? "remote_on" : "remote_off", null, now, ct);
        return Result.Success(new PolicyChange<AiSettingsView>(await AiSettingsViews.ReadAsync(settings, ct), Changed: true));
    }
}

[RequestHandler<SetWorkerSafeCommand, PolicyChange<WorkerView>>]
public sealed class SetWorkerSafeHandler(IWorkerStore store, INidKeys keys, IWorkerDirectory directory, INotifier notifier, IClock clock)
    : IRequestHandler<SetWorkerSafeCommand, PolicyChange<WorkerView>>
{
    public async ValueTask<Result<PolicyChange<WorkerView>>> HandleAsync(SetWorkerSafeCommand command, CancellationToken ct = default)
    {
        if (await store.FindAsync(command.Id, ct) is { } current && current.Status != WorkerStatus.Revoked && current.Safe == command.Safe)
            return Result.Success(new PolicyChange<WorkerView>(await WorkerViews.FromAsync(current, keys, clock, ct), Changed: false));

        var result = await WorkerManagement.ChangeAsync(command.Id, store, keys, directory, clock, ct,
            w => WorkerRules.Revoke(w.Status), w => w with { Safe = command.Safe });
        if (!result.TryGetValue(out var view))
            return result.TryGetFailure(out var failure) ? Result.Failure<PolicyChange<WorkerView>>(failure!) : throw new InvalidOperationException();

        await DataPolicyNotices.TellAsync(notifier, command.Safe ? "safe_on" : "safe_off", view.Name, clock.GetCurrentInstant(), ct);
        return Result.Success(new PolicyChange<WorkerView>(view, Changed: true));
    }
}

[RequestHandler<GetAiSettingsQuery, AiSettingsView>]
public sealed class GetAiSettingsHandler(IAiSettings settings) : IRequestHandler<GetAiSettingsQuery, AiSettingsView>
{
    public async ValueTask<Result<AiSettingsView>> HandleAsync(GetAiSettingsQuery query, CancellationToken ct = default)
        => Result.Success(await AiSettingsViews.ReadAsync(settings, ct));
}

/// <summary>#556: the document types a cloud provider may read (<c>allowed_kinds</c>, <c>docs/ai/README.md</c> §6.1): an admin,
/// in the browser. None by default; a payslip is never offered. Every adult is told of a change.</summary>
public sealed record SetAllowedKindsCommand(IReadOnlyList<string> Kinds) : ICommand<PolicyChange<AiSettingsView>>, IAdminOnly, IBrowserOnly;

[RequestHandler<SetAllowedKindsCommand, PolicyChange<AiSettingsView>>]
public sealed class SetAllowedKindsHandler(IAiSettings settings, INotifier notifier, IClock clock)
    : IRequestHandler<SetAllowedKindsCommand, PolicyChange<AiSettingsView>>
{
    public async ValueTask<Result<PolicyChange<AiSettingsView>>> HandleAsync(SetAllowedKindsCommand command, CancellationToken ct = default)
    {
        if (command.Kinds.FirstOrDefault(k => !CloudKinds.All.Contains(k)) is { } unknown)
            return Result.Failure<PolicyChange<AiSettingsView>>(new Failure(JobCodes.UnknownKind, $"'{unknown}' may not go to a cloud provider.", null));

        // Kept in the list's order, once each.
        var kinds = CloudKinds.All.Where(command.Kinds.Contains).ToList();
        if ((await settings.AllowedKindsAsync(ct)).SequenceEqual(kinds))
            return Result.Success(new PolicyChange<AiSettingsView>(await AiSettingsViews.ReadAsync(settings, ct), Changed: false));
        if (!await settings.SetAllowedKindsAsync(kinds, ct))
            return Result.Failure<PolicyChange<AiSettingsView>>(
                new ConflictFailure("Another adult changed it at the same moment: read it again.") { Code = JobCodes.SettingsChanged });
        await DataPolicyNotices.TellAsync(notifier, "allowed_kinds", null, clock.GetCurrentInstant(), ct,
            new Dictionary<string, string> { ["kinds"] = string.Join(',', kinds) });
        return Result.Success(new PolicyChange<AiSettingsView>(await AiSettingsViews.ReadAsync(settings, ct), Changed: true));
    }
}
