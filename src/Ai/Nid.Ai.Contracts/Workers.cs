using NodaTime;

namespace Nid.Ai.Contracts;

public static class AiCodes
{
    public const string WorkerNotFound = "ai.worker_not_found";
    public const string WorkerExists = "ai.worker_exists";
    public const string WorkerUnreachable = "ai.worker_unreachable";
    public const string WorkerNotPending = "ai.worker_not_pending";
    public const string WorkerNotActive = "ai.worker_not_active";
    public const string WorkerNotDisabled = "ai.worker_not_disabled";
    public const string WorkerRevoked = "ai.worker_revoked";
    public const string InvalidName = "ai.invalid_name";
    public const string InvalidUrl = "ai.invalid_url";
    public const string FingerprintMismatch = "ai.fingerprint_mismatch";
    public const string PairingRefused = "ai.pairing_refused";
    public const string WorkerRefused = "ai.worker_refused";
}

/// <summary>Where a worker runs, set by the admin who registers it, never by the worker: <c>local</c> (this machine or
/// its network) or <c>remote</c> (off the premises).</summary>
public enum WorkerPlacement
{
    Local,
    Remote,
}

public enum WorkerStatus
{
    Pending,
    Active,
    Disabled,
    Revoked,
}

/// <summary>A worker as the settings show it: fingerprints, never keys.</summary>
public sealed record WorkerView(
    Guid Id,
    string Name,
    string Url,
    WorkerPlacement Placement,
    bool ProxyDeclared,
    bool Safe,
    WorkerStatus Status,
    string KeyFingerprint,
    string? NidKeyFingerprint,
    bool NidKeyRotationOverdue,
    string? SoftwareVersion,
    Instant? LastSeenAt,
    Instant CreatedAt,
    Instant? PairedAt);

public sealed record WorkerList(IReadOnlyList<WorkerView> Workers);

/// <summary>A registration waiting for its pairing: the fingerprint the admin compares with the worker's console.</summary>
public sealed record RegisteredWorker(Guid Id, string Fingerprint);

/// <summary>Nid's new signing key after a rotation, to confirm on each worker's console, and the workers that could not
/// be told.</summary>
public sealed record NidKeyRotation(string Fingerprint, IReadOnlyList<Guid> Unreached);
