using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Keys;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

public sealed record RenameWorkerCommand(Guid Id, string Name) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => WorkerManagement.Event(SecurityEventNames.ModelWorkerChanged, response, "renamed");
}

public sealed record DisableWorkerCommand(Guid Id) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => WorkerManagement.Event(SecurityEventNames.ModelWorkerChanged, response, "disabled");
}

public sealed record EnableWorkerCommand(Guid Id) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => WorkerManagement.Event(SecurityEventNames.ModelWorkerChanged, response, "enabled");
}

/// <summary>Final: the worker's keys stop being accepted at once and its address leaves the registered endpoints.</summary>
public sealed record RevokeWorkerCommand(Guid Id) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => WorkerManagement.Event(SecurityEventNames.ModelWorkerRevoked, response);
}

/// <summary>The worker makes a new key and announces it in a response only its old key could produce; its old key keeps
/// opening requests for a day.</summary>
public sealed record RotateWorkerKeyCommand(Guid Id) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => WorkerManagement.Event(SecurityEventNames.ModelWorkerKeyRotated, response);
}

/// <summary>Reads a worker's declaration again, and moves it to Nid's new key once its operator confirmed it.</summary>
public sealed record RefreshWorkerCommand(Guid Id) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => null;
}

/// <summary>A new Nid signing key, sent to every active worker signed by the key it pinned; each operator confirms its
/// fingerprint on the worker's console, then a refresh moves the worker to it.</summary>
public sealed record RotateNidKeyCommand : ICommand<NidKeyRotation>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response)
        => response is NidKeyRotation rotation
            ? new SecurityEvent(SecurityEventNames.NidWorkerKeyRotated, null, [], new Dictionary<string, string> { ["fingerprint"] = rotation.Fingerprint })
            : null;
}

internal static class WorkerManagement
{
    public static SecurityEvent? Event(string name, object response, string? change = null)
        => response is WorkerView view ? WorkerEvents.Of(name, view.Id, view.Name, change) : null;

    /// <summary>Loads the worker, applies a rule and a change, saves, refreshes the outbound directory.</summary>
    public static async Task<Result<WorkerView>> ChangeAsync(
        Guid id, IWorkerStore store, INidKeys keys, IWorkerDirectory directory, IClock clock, CancellationToken ct,
        Func<WorkerRecord, string?> rule, Func<WorkerRecord, WorkerRecord> change)
    {
        if (await store.FindAsync(id, ct) is not { } worker)
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerNotFound);
        if (rule(worker) is { } refusal)
            return WorkerFailures.Refused<WorkerView>(refusal);

        var changed = change(worker);
        if (!await store.UpdateAsync(changed, ct))
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerChanged);
        directory.Invalidate();
        return Result.Success(await WorkerViews.FromAsync(changed, keys, clock, ct));
    }
}

[RequestHandler<RenameWorkerCommand, WorkerView>]
public sealed class RenameWorkerHandler(IWorkerStore store, INidKeys keys, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<RenameWorkerCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(RenameWorkerCommand command, CancellationToken ct = default)
        => await WorkerManagement.ChangeAsync(command.Id, store, keys, directory, clock, ct,
            w => WorkerRules.Rename(w.Status, command.Name), w => w with { Name = command.Name.Trim() });
}

[RequestHandler<DisableWorkerCommand, WorkerView>]
public sealed class DisableWorkerHandler(IWorkerStore store, INidKeys keys, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<DisableWorkerCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(DisableWorkerCommand command, CancellationToken ct = default)
        => await WorkerManagement.ChangeAsync(command.Id, store, keys, directory, clock, ct,
            w => WorkerRules.Disable(w.Status), w => w with { Status = WorkerStatus.Disabled });
}

[RequestHandler<EnableWorkerCommand, WorkerView>]
public sealed class EnableWorkerHandler(IWorkerStore store, INidKeys keys, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<EnableWorkerCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(EnableWorkerCommand command, CancellationToken ct = default)
        => await WorkerManagement.ChangeAsync(command.Id, store, keys, directory, clock, ct,
            w => WorkerRules.Enable(w.Status), w => w with { Status = WorkerStatus.Active });
}

[RequestHandler<RevokeWorkerCommand, WorkerView>]
public sealed class RevokeWorkerHandler(IWorkerStore store, INidKeys keys, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<RevokeWorkerCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(RevokeWorkerCommand command, CancellationToken ct = default)
        => await WorkerManagement.ChangeAsync(command.Id, store, keys, directory, clock, ct,
            w => WorkerRules.Revoke(w.Status), w => w with { Status = WorkerStatus.Revoked, RevokedAt = clock.GetCurrentInstant() });
}

[RequestHandler<RotateWorkerKeyCommand, WorkerView>]
public sealed class RotateWorkerKeyHandler(IWorkerStore store, IWorkerClient client, INidKeys keys, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<RotateWorkerKeyCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(RotateWorkerKeyCommand command, CancellationToken ct = default)
    {
        if (await store.FindAsync(command.Id, ct) is not { } worker)
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerNotFound);
        if (WorkerRules.RotateKey(worker.Status) is { } refusal)
            return WorkerFailures.Refused<WorkerView>(refusal);

        byte[] next;
        try
        {
            next = await client.RotateWorkerKeyAsync(worker, (await keys.FindAsync(worker.NidKeyId!.Value, ct))!, ct);
        }
        catch (WorkerCallException e)
        {
            return WorkerFailures.Refused<WorkerView>(e.Code);
        }

        var now = clock.GetCurrentInstant();
        var rotated = worker with
        {
            PreviousWorkerPublicKey = worker.WorkerPublicKey,
            PreviousWorkerKeyUntil = now + Duration.FromTimeSpan(WorkerRules.KeyOverlap),
            WorkerPublicKey = next,
            WorkerKeyFingerprint = KeyFingerprint.Compact(next),
            LastSeenAt = now,
        };
        if (!await store.UpdateAsync(rotated, ct))
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerChanged);
        directory.Invalidate();
        return Result.Success(await WorkerViews.FromAsync(rotated, keys, clock, ct));
    }
}

[RequestHandler<RefreshWorkerCommand, WorkerView>]
public sealed class RefreshWorkerHandler(IWorkerStore store, IWorkerClient client, INidKeys keys, IClock clock)
    : IRequestHandler<RefreshWorkerCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(RefreshWorkerCommand command, CancellationToken ct = default)
    {
        if (await store.FindAsync(command.Id, ct) is not { } worker)
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerNotFound);
        if (WorkerRules.Call(worker.Status) is { } refusal)
            return WorkerFailures.Refused<WorkerView>(refusal);

        var active = await keys.ActiveAsync(ct);
        var pinned = worker.NidKeyId is { } id ? await keys.FindAsync(id, ct) : null;

        // Nid's newest key first: it works once the operator confirmed it on the worker's console.
        WorkerDeclaration? declaration = null;
        NidKey? used = null;
        string? failure = null;
        foreach (var key in new[] { active, pinned }.OfType<NidKey>().DistinctBy(k => k.Id))
        {
            try
            {
                declaration = await client.DeclarationAsync(worker, key, ct);
                used = key;
                break;
            }
            catch (WorkerCallException e)
            {
                failure = e.Code;
            }
        }

        if (declaration is null || used is null)
            return WorkerFailures.Refused<WorkerView>(failure ?? AiCodes.WorkerRefused);

        var now = clock.GetCurrentInstant();
        var refreshed = worker with
        {
            NidKeyId = declaration.NidKeyFingerprint == used.Fingerprint ? used.Id : worker.NidKeyId,
            LastDeclaration = JsonSerializer.Serialize(declaration, ContractJson.Strict),
            SoftwareVersion = declaration.SoftwareVersion,
            LastSeenAt = now,
        };
        if (!await store.UpdateAsync(refreshed, ct))
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerChanged);
        await keys.RetireUnusedAsync(ct);
        return Result.Success(await WorkerViews.FromAsync(refreshed, keys, clock, ct));
    }
}

[RequestHandler<RotateNidKeyCommand, NidKeyRotation>]
public sealed class RotateNidKeyHandler(IWorkerStore store, IWorkerClient client, INidKeys keys)
    : IRequestHandler<RotateNidKeyCommand, NidKeyRotation>
{
    public async ValueTask<Result<NidKeyRotation>> HandleAsync(RotateNidKeyCommand command, CancellationToken ct = default)
    {
        var next = await keys.RotateAsync(ct);
        var unreached = new List<Guid>();
        foreach (var worker in (await store.ListAsync(ct)).Where(w => w.Status is WorkerStatus.Active or WorkerStatus.Disabled))
        {
            // A disabled worker is not called: it keeps the old key, and the admin is told so.
            if (worker.Status == WorkerStatus.Disabled)
            {
                unreached.Add(worker.Id);
                continue;
            }

            try
            {
                var pinned = await keys.FindAsync(worker.NidKeyId!.Value, ct);
                await client.SendNidRotationAsync(worker, pinned!, next, ct);
            }
            catch (WorkerCallException)
            {
                unreached.Add(worker.Id);
            }
        }

        return Result.Success(new NidKeyRotation(KeyFingerprint.Of(next.Key.ExportSubjectPublicKeyInfo()), unreached));
    }
}
