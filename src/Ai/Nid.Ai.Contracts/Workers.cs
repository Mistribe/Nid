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
    public const string WorkerChanged = "ai.worker_changed";
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

/// <summary>A worker as the settings show it: fingerprints, never keys. <c>placement</c> is <c>local</c> or <c>remote</c>,
/// <c>status</c> <c>pending</c>, <c>active</c>, <c>disabled</c> or <c>revoked</c>.</summary>
public sealed record WorkerView(
    Guid Id,
    string Name,
    string Url,
    string Placement,
    bool ProxyDeclared,
    bool Safe,
    string Status,
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

/// <summary>The household's AI settings as any adult reads them.</summary>
public sealed record AiSettingsView(bool RemoteWorkersAllowed);

/// <summary>« Tester »: how many frames the streamed echo answered, and the echo job queued for the worker.</summary>
public sealed record WorkerTestResult(int Frames, Guid JobId);

/// <summary>Nid's new signing key after a rotation, to confirm on each worker's console, and the workers that could not
/// be told.</summary>
public sealed record NidKeyRotation(string Fingerprint, IReadOnlyList<Guid> Unreached);

/// <summary>What a job or request carries (<c>docs/ai/README.md</c> §6.1): its class decides which workers may see it.</summary>
public static class DataClasses
{
    /// <summary>No household data (an admin's « Tester »).</summary>
    public const string None = "none";

    /// <summary>The content of a statement, an amortization table, an invoice, a contract.</summary>
    public const string Documents = "documents";

    /// <summary>A « local seulement » document; payslips by default (§6.6).</summary>
    public const string LocalOnly = "local_only";

    /// <summary>Structured figures: amounts, income, lines, taxes.</summary>
    public const string Figures = "figures";

    /// <summary>Masked bank label patterns (#450).</summary>
    public const string BankLabels = "bank_labels";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { None, Documents, LocalOnly, Figures, BankLabels };

    public static bool IsKnown(string dataClass) => All.Contains(dataClass);
}

/// <summary>What model-backed features can run now (decision D, <c>docs/ai/README.md</c> §11): for <c>echo</c> and each
/// capability a worker declares, the destinations online, or why none.</summary>
public sealed record ModelCapabilities(bool RemoteWorkersAllowed, IReadOnlyList<CapabilityView> Capabilities);

/// <summary>Online workers serving a capability: <see cref="Local"/>, <see cref="Remote"/> (counted only with the household's
/// opt-in) and among them <see cref="Safe"/>; <see cref="Reason"/> when none: <c>no_worker</c>, <c>remote_not_allowed</c>,
/// <c>all_offline</c>.</summary>
public sealed record CapabilityView(string Capability, int Local, int Remote, int Safe, string? Reason);
