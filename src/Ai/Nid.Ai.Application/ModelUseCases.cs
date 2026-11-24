using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>How far one use may go (decision H, §12): an admin, in the browser (#585). Opening a use to the cloud, or closing
/// it, tells every adult.</summary>
public sealed record SetUseModeCommand(string Use, string Mode) : ICommand<PolicyChange<AiSettingsView>>, IAdminOnly, IBrowserOnly;

/// <summary>The model one use runs on (§12): an admin, among the models the workers declare or, for <c>embed</c> (#556), the
/// model of an enabled cloud profile that embeds; null clears it.</summary>
public sealed record SetUseModelCommand(string Use, string? Model) : ICommand<AiSettingsView>, IAdminOnly, IBrowserOnly;

/// <summary>The models the active workers declare, for the model of each use (any adult reads them).</summary>
public sealed record ListDeclaredModelsQuery : IQuery<DeclaredModels>;

internal static class AiSettingsViews
{
    public static async Task<AiSettingsView> ReadAsync(IAiSettings settings, CancellationToken ct)
    {
        var uses = await settings.UsesAsync(ct);
        return new AiSettingsView(await settings.RemoteWorkersAllowedAsync(ct),
            [.. ModelUses.All.Select(u => new UseSettingView(u, uses[u].Mode, uses[u].Model))], await settings.AllowedKindsAsync(ct),
            await settings.ConversationRetentionDaysAsync(ct));
    }
}

[RequestHandler<SetUseModeCommand, PolicyChange<AiSettingsView>>]
public sealed class SetUseModeHandler(IAiSettings settings, INotifier notifier, IClock clock)
    : IRequestHandler<SetUseModeCommand, PolicyChange<AiSettingsView>>
{
    public async ValueTask<Result<PolicyChange<AiSettingsView>>> HandleAsync(SetUseModeCommand command, CancellationToken ct = default)
    {
        if (!ModelUses.All.Contains(command.Use))
            return Result.Failure<PolicyChange<AiSettingsView>>(new Failure(JobCodes.UnknownUse, "No such use.", null));
        if (!UseModes.All.Contains(command.Mode))
            return Result.Failure<PolicyChange<AiSettingsView>>(new Failure(JobCodes.UnknownMode, "No such mode.", null));

        var before = (await settings.UseAsync(command.Use, ct)).Mode;
        if (before == command.Mode)
            return Result.Success(new PolicyChange<AiSettingsView>(await AiSettingsViews.ReadAsync(settings, ct), Changed: false));
        if (!await settings.SetUseModeAsync(command.Use, command.Mode, ct))
            return Result.Failure<PolicyChange<AiSettingsView>>(
                new ConflictFailure("Another adult changed it at the same moment: read it again.") { Code = JobCodes.SettingsChanged });

        var cloudBefore = before == UseModes.SelfHostedThenCloud;
        var cloudNow = command.Mode == UseModes.SelfHostedThenCloud;
        if (cloudBefore != cloudNow)
            await DataPolicyNotices.TellAsync(notifier, cloudNow ? "cloud_on" : "cloud_off", null, clock.GetCurrentInstant(), ct,
                new Dictionary<string, string> { ["use"] = command.Use });
        return Result.Success(new PolicyChange<AiSettingsView>(await AiSettingsViews.ReadAsync(settings, ct), Changed: true));
    }
}

[RequestHandler<SetUseModelCommand, AiSettingsView>]
public sealed class SetUseModelHandler(IAiSettings settings, IWorkerStore workers, ILlmProfiles profiles, ICloudSettings cloud)
    : IRequestHandler<SetUseModelCommand, AiSettingsView>
{
    public async ValueTask<Result<AiSettingsView>> HandleAsync(SetUseModelCommand command, CancellationToken ct = default)
    {
        if (!ModelUses.All.Contains(command.Use))
            return Result.Failure<AiSettingsView>(new Failure(JobCodes.UnknownUse, "No such use.", null));
        if (command.Model is { } model)
        {
            var declared = await DeclaredModelsHandler.ListAsync(workers, ct);
            if (!declared.Any(m => m.Name == model && m.Capability == ModelUses.Capability(command.Use))
                && !(command.Use == ModelUses.Embed && (await CloudEmbedModels.EnabledAsync(profiles, cloud, ct)).Contains(model)))
                return Result.Failure<AiSettingsView>(new Failure(JobCodes.ModelUnavailable, "No active worker declares this model for this use.", null));
        }

        if (!await settings.SetUseModelAsync(command.Use, command.Model, ct))
            return Result.Failure<AiSettingsView>(
                new ConflictFailure("Another adult changed it at the same moment: read it again.") { Code = JobCodes.SettingsChanged });
        return Result.Success(await AiSettingsViews.ReadAsync(settings, ct));
    }
}

[RequestHandler<ListDeclaredModelsQuery, DeclaredModels>]
public sealed class DeclaredModelsHandler(IWorkerStore workers) : IRequestHandler<ListDeclaredModelsQuery, DeclaredModels>
{
    public async ValueTask<Result<DeclaredModels>> HandleAsync(ListDeclaredModelsQuery query, CancellationToken ct = default)
        => Result.Success(new DeclaredModels(await ListAsync(workers, ct)));

    /// <summary>Every model an active worker declares, by name and capability, with how many active workers serve it.</summary>
    internal static async Task<IReadOnlyList<DeclaredModelView>> ListAsync(IWorkerStore workers, CancellationToken ct)
        => [.. (await workers.ListAsync(ct))
            .Where(w => w.Status == WorkerStatus.Active)
            .SelectMany(w => (WorkerRouter.DeclarationOf(w)?.Models ?? []).Select(m => (m.Name, Capability: JsonNamingPolicy.KebabCaseLower.ConvertName(m.Capability.ToString()), w.Id)))
            .GroupBy(m => (m.Name, m.Capability))
            .OrderBy(g => g.Key.Capability, StringComparer.Ordinal).ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .Select(g => new DeclaredModelView(g.Key.Name, g.Key.Capability, g.Select(m => m.Id).Distinct().Count()))];
}
