using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Keys;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>A command on the worker registry: an admin's, in the browser, recorded as a security event
/// (<c>docs/ai/README.md</c> §12).</summary>
public interface IWorkerAdminCommand : IAdminOnly, IBrowserOnly, IRecordsSecurityEvent;

/// <summary>Registers a worker by its address and reads its key, for the admin to compare with the worker's console.
/// Nothing is pinned until <see cref="PairWorkerCommand"/>.</summary>
public sealed record RegisterWorkerCommand(string Name, string Url, WorkerPlacement Placement, bool ProxyDeclared)
    : ICommand<RegisteredWorker>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response)
        => response is RegisteredWorker registered ? WorkerEvents.Of(SecurityEventNames.ModelWorkerRegistered, registered.Id, Name) : null;
}

/// <summary>Pairs a registered worker: the fingerprint the admin confirmed, the code read off its console.</summary>
public sealed record PairWorkerCommand(Guid Id, string Fingerprint, string Code) : ICommand<WorkerView>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response)
        => response is WorkerView view ? WorkerEvents.Of(SecurityEventNames.ModelWorkerPaired, view.Id, view.Name) : null;
}

/// <summary>Every worker, for any adult; fingerprints only.</summary>
public sealed record ListWorkersQuery : IQuery<WorkerList>;

internal static class WorkerEvents
{
    public static SecurityEvent Of(string name, Guid worker, string workerName, string? change = null)
    {
        var data = new Dictionary<string, string> { ["worker"] = worker.ToString(), ["name"] = workerName };
        if (change is not null)
            data["change"] = change;
        return new SecurityEvent(name, null, [], data);
    }
}

internal static class WorkerFailures
{
    public static Result<T> Refused<T>(string code) where T : notnull => code switch
    {
        AiCodes.WorkerNotFound => Result.Failure<T>(new NotFoundFailure("worker", "") { Code = code }),
        AiCodes.WorkerExists or AiCodes.WorkerNotPending or AiCodes.WorkerNotActive or AiCodes.WorkerNotDisabled or AiCodes.WorkerRevoked
            => Result.Failure<T>(new ConflictFailure(Messages.GetValueOrDefault(code, code)) { Code = code }),
        _ => Result.Failure<T>(new Failure(code, Messages.GetValueOrDefault(code, code), null)),
    };

    private static readonly Dictionary<string, string> Messages = new()
    {
        [AiCodes.WorkerExists] = "A worker is already registered at this address.",
        [AiCodes.WorkerUnreachable] = "The worker could not be reached at this address.",
        [AiCodes.WorkerNotPending] = "This worker is already paired.",
        [AiCodes.WorkerNotActive] = "This worker is not active.",
        [AiCodes.WorkerNotDisabled] = "This worker is not disabled.",
        [AiCodes.WorkerRevoked] = "This worker is revoked.",
        [AiCodes.InvalidName] = "A worker's name has 1 to 60 characters.",
        [AiCodes.InvalidUrl] = "A worker's address is https://host or https://host:port.",
        [AiCodes.FingerprintMismatch] = "The fingerprint does not match the worker's key.",
        [AiCodes.PairingRefused] = "The worker refused the pairing: check the code, or get a new one on its console.",
        [AiCodes.WorkerRefused] = "The worker refused the request.",
    };
}

public static class WorkerViews
{
    public static WorkerView From(WorkerRecord worker, NidKey? workerKey, NidKey? activeKey, Instant now) => new(
        worker.Id, worker.Name, worker.Url.ToString().TrimEnd('/'),
        worker.Placement == WorkerPlacement.Local ? "local" : "remote", worker.ProxyDeclared, worker.Safe,
        worker.Status.ToString().ToLowerInvariant(), KeyFingerprint.Of(worker.WorkerPublicKey), workerKey?.Fingerprint,
        NidKeyRotationOverdue: workerKey is { Active: false } && activeKey is not null
                               && now - activeKey.CreatedAt > Duration.FromTimeSpan(WorkerRules.RotationOverdue),
        worker.SoftwareVersion, worker.LastSeenAt, worker.CreatedAt, worker.PairedAt);

    public static async Task<WorkerView> FromAsync(WorkerRecord worker, INidKeys keys, IClock clock, CancellationToken ct)
        => From(worker, worker.NidKeyId is { } id ? await keys.FindAsync(id, ct) : null, await keys.ActiveAsync(ct), clock.GetCurrentInstant());
}

[RequestHandler<RegisterWorkerCommand, RegisteredWorker>]
public sealed class RegisterWorkerHandler(IWorkerStore store, IWorkerClient client, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<RegisterWorkerCommand, RegisteredWorker>
{
    public async ValueTask<Result<RegisteredWorker>> HandleAsync(RegisterWorkerCommand command, CancellationToken ct = default)
    {
        var refusal = WorkerRules.Url(command.Url, out var url) ?? WorkerRules.Name(command.Name);
        if (refusal is not null)
            return WorkerFailures.Refused<RegisteredWorker>(refusal);

        var now = clock.GetCurrentInstant();
        await store.DropPendingCreatedBeforeAsync(now - Duration.FromTimeSpan(WorkerRules.PendingLifetime), ct);

        var existing = await store.FindByEndpointAsync(url!.IdnHost, url.Port, ct);
        if (existing is { Status: not WorkerStatus.Pending })
            return WorkerFailures.Refused<RegisteredWorker>(AiCodes.WorkerExists);

        // Registered first, so Myelin lets the address through when it is a private one; its key comes after.
        var worker = existing ?? new WorkerRecord(Guid.CreateVersion7(), command.Name.Trim(), url.IdnHost, url.Port, command.ProxyDeclared,
            command.Placement, Safe: false, WorkerStatus.Pending, new byte[65], new string('0', 64), null, null, null, null, null, null,
            null, now, null, null, 0);
        if (existing is null)
            await store.AddAsync(worker, ct);
        directory.Invalidate();

        WorkerKey key;
        try
        {
            key = await client.GetKeyAsync(worker.Url, ct);
        }
        catch (WorkerCallException e)
        {
            return WorkerFailures.Refused<RegisteredWorker>(e.Code);
        }

        await store.UpdateAsync((await store.FindAsync(worker.Id, ct))! with
        {
            Name = command.Name.Trim(),
            ProxyDeclared = command.ProxyDeclared,
            Placement = command.Placement,
            WorkerPublicKey = key.PublicKey,
            WorkerKeyFingerprint = KeyFingerprint.Compact(key.PublicKey),
        }, ct);
        return Result.Success(new RegisteredWorker(worker.Id, key.Fingerprint));
    }
}

[RequestHandler<PairWorkerCommand, WorkerView>]
public sealed class PairWorkerHandler(IWorkerStore store, IWorkerClient client, INidKeys keys, IWorkerDirectory directory, IClock clock)
    : IRequestHandler<PairWorkerCommand, WorkerView>
{
    public async ValueTask<Result<WorkerView>> HandleAsync(PairWorkerCommand command, CancellationToken ct = default)
    {
        if (await store.FindAsync(command.Id, ct) is not { } worker)
            return WorkerFailures.Refused<WorkerView>(AiCodes.WorkerNotFound);
        if (WorkerRules.Pair(worker.Status) is { } refusal)
            return WorkerFailures.Refused<WorkerView>(refusal);
        if (Compact(command.Fingerprint) != worker.WorkerKeyFingerprint)
            return WorkerFailures.Refused<WorkerView>(AiCodes.FingerprintMismatch);

        var key = await keys.ActiveAsync(ct);
        PairResponse response;
        try
        {
            response = await client.PairAsync(worker, key, Code(command.Code), ct);
        }
        catch (WorkerCallException e)
        {
            return WorkerFailures.Refused<WorkerView>(e.Code);
        }

        if (response.Declaration.NidKeyFingerprint != key.Fingerprint)
            return WorkerFailures.Refused<WorkerView>(AiCodes.PairingRefused);

        var now = clock.GetCurrentInstant();
        var paired = worker with
        {
            Status = WorkerStatus.Active,
            PairedAt = now,
            LastSeenAt = now,
            NidKeyId = key.Id,
            TlsCertificateSha256 = worker.ProxyDeclared ? null : response.TlsCertificateSha256,
            LastDeclaration = JsonSerializer.Serialize(response.Declaration, ContractJson.Strict),
            SoftwareVersion = response.Declaration.SoftwareVersion,
        };
        await store.UpdateAsync(paired, ct);
        directory.Invalidate();
        return Result.Success(await WorkerViews.FromAsync(paired, keys, clock, ct));
    }

    internal static string Compact(string fingerprint) => fingerprint.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>The console shows <c>ABCD-EFGH</c>; a person may type it in lower case, with or without the dash.</summary>
    private static string Code(string code) => code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}

[RequestHandler<ListWorkersQuery, WorkerList>]
public sealed class ListWorkersHandler(IWorkerStore store, INidKeys keys, IClock clock) : IRequestHandler<ListWorkersQuery, WorkerList>
{
    public async ValueTask<Result<WorkerList>> HandleAsync(ListWorkersQuery query, CancellationToken ct = default)
    {
        var workers = await store.ListAsync(ct);
        if (workers.Count == 0)
            return Result.Success(new WorkerList([]));

        var active = await keys.ActiveAsync(ct);
        var views = new List<WorkerView>();
        foreach (var worker in workers.Where(w => w.Status != WorkerStatus.Revoked))
            views.Add(WorkerViews.From(worker, worker.NidKeyId is { } id ? await keys.FindAsync(id, ct) : null, active, clock.GetCurrentInstant()));
        return Result.Success(new WorkerList(views));
    }
}
