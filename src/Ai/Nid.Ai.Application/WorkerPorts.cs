using System.Security.Cryptography;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using NodaTime;

namespace Nid.Ai.Application;

/// <summary>A registered worker as the use cases see it.</summary>
public sealed record WorkerRecord(
    Guid Id,
    string Name,
    string Host,
    int Port,
    bool ProxyDeclared,
    WorkerPlacement Placement,
    bool Safe,
    WorkerStatus Status,
    byte[] WorkerPublicKey,
    string WorkerKeyFingerprint,
    byte[]? PreviousWorkerPublicKey,
    Instant? PreviousWorkerKeyUntil,
    string? TlsCertificateSha256,
    Guid? NidKeyId,
    string? LastDeclaration,
    Instant? LastSeenAt,
    string? SoftwareVersion,
    Instant CreatedAt,
    Instant? PairedAt,
    Instant? RevokedAt,
    uint Version)
{
    public Uri Url => Domain.WorkerRules.ToUrl(Host, Port);

    /// <summary>False while registration could not read the worker's key: nothing to compare or seal to yet.</summary>
    public bool KeyRead => WorkerKeyFingerprint != NoKeyFingerprint;

    public static readonly string NoKeyFingerprint = new('0', 64);
}

public interface IWorkerStore
{
    Task<IReadOnlyList<WorkerRecord>> ListAsync(CancellationToken ct);

    Task<WorkerRecord?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>A worker not revoked at this host and port.</summary>
    Task<WorkerRecord?> FindByEndpointAsync(string host, int port, CancellationToken ct);

    Task AddAsync(WorkerRecord worker, CancellationToken ct);

    /// <summary>Saves a change; false when the worker changed since <see cref="WorkerRecord.Version"/> was read.</summary>
    Task<bool> UpdateAsync(WorkerRecord worker, CancellationToken ct);

    Task DropPendingCreatedBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>One of Nid's signing keys towards workers.</summary>
public sealed record NidKey(Guid Id, ECDsa Key, string Fingerprint, bool Active, Instant CreatedAt);

public interface INidKeys
{
    /// <summary>The active key, created the first time it is needed.</summary>
    Task<NidKey> ActiveAsync(CancellationToken ct);

    Task<NidKey?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>A new active key; the previous one becomes retiring.</summary>
    Task<NidKey> RotateAsync(CancellationToken ct);

    /// <summary>Deletes the retiring keys no worker uses any more.</summary>
    Task RetireUnusedAsync(CancellationToken ct);
}

/// <summary>Registry changes Nid's outbound client must see at once (registered endpoints, TLS pins).</summary>
public interface IWorkerDirectory
{
    void Invalidate();
}

/// <summary>The worker's public key and fingerprint, read at registration before anything is pinned.</summary>
public sealed record WorkerKey(byte[] PublicKey, string Fingerprint);

/// <summary>A call to a worker that failed: unreachable, or refused (<see cref="AiCodes"/>).</summary>
public sealed class WorkerCallException(string code) : Exception($"Worker call failed: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>Nid's calls to its workers: sealed to the worker's pinned key, signed with the Nid key it pinned. A disabled
/// or revoked worker is refused before anything is sent.</summary>
public interface IWorkerClient
{
    Task<WorkerKey> GetKeyAsync(Uri url, CancellationToken ct);

    Task<PairResponse> PairAsync(WorkerRecord worker, NidKey key, string code, CancellationToken ct);

    Task<WorkerDeclaration> DeclarationAsync(WorkerRecord worker, NidKey key, CancellationToken ct);

    Task<byte[]> RotateWorkerKeyAsync(WorkerRecord worker, NidKey key, CancellationToken ct);

    Task SendNidRotationAsync(WorkerRecord worker, NidKey key, NidKey next, CancellationToken ct);

    /// <summary>Sends a job; the answer is the worker's id for it and the secret its result will be bound to.</summary>
    Task<SubmittedJob> SubmitJobAsync(WorkerRecord worker, NidKey key, Protocol.Contracts.JobSubmission submission, CancellationToken ct);

    /// <summary>A job's status; once it succeeded, its result opened with the secret of its submission.</summary>
    Task<PolledJob> PollJobAsync(WorkerRecord worker, NidKey key, string workerJobId, byte[] responseSecret, CancellationToken ct);

    Task CancelJobAsync(WorkerRecord worker, NidKey key, string workerJobId, CancellationToken ct);

    /// <summary>A request's answer, frame by frame as the worker sends it; a call that cannot start, or a stream that breaks,
    /// throws <see cref="WorkerCallException"/>.</summary>
    IAsyncEnumerable<Protocol.Framing.Frame> StreamAsync(
        WorkerRecord worker, NidKey key, string type, int version, byte[] input, Protocol.Framing.FrameLimits limits, CancellationToken ct);
}

public sealed record SubmittedJob(string WorkerJobId, byte[] ResponseSecret);

/// <summary>A job's status, and once it succeeded its result, opened; null when there is none or it could not be opened
/// (frames that do not read under the submission's secret, or a stream cut before its end).</summary>
public sealed record PolledJob(Protocol.Contracts.JobStatus Status, byte[]? Result);
