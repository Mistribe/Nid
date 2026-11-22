using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>A registered worker (<c>ai.model_worker</c>, <c>docs/ai/README.md</c> §13). Placement and <c>safe</c> are
/// set by people in Nid, never by the worker; its keys are public keys, its identity the pinned one.</summary>
public sealed class ModelWorkerRow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Host { get; set; }

    public int Port { get; set; }

    public bool ProxyDeclared { get; set; }

    /// <summary><c>local</c> or <c>remote</c>.</summary>
    public required string Placement { get; set; }

    public bool Safe { get; set; }

    /// <summary><c>pending</c>, <c>active</c>, <c>disabled</c> or <c>revoked</c>.</summary>
    public required string Status { get; set; }

    /// <summary>The worker's HPKE public key (65 bytes), pinned at registration and confirmed by the admin.</summary>
    public required byte[] WorkerPublicKey { get; set; }

    public required string WorkerKeyFingerprint { get; set; }

    public byte[]? PreviousWorkerPublicKey { get; set; }

    public Instant? PreviousWorkerKeyUntil { get; set; }

    /// <summary>SHA-256 of the worker's TLS certificate, pinned when Nid reaches it directly; null behind a declared proxy.</summary>
    public string? TlsCertificateSha256 { get; set; }

    /// <summary>The Nid key this worker pinned: every request to it is signed with that one.</summary>
    public Guid? NidKeyId { get; set; }

    public string? LastDeclaration { get; set; }

    public Instant? LastSeenAt { get; set; }

    public string? SoftwareVersion { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant? PairedAt { get; set; }

    public Instant? RevokedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>A Nid signing key towards workers (<c>ai.ai_key</c>): the private key encrypted by Data Protection, as
/// provider API keys are. One is <c>active</c>; after a rotation the previous one is <c>retiring</c> until no worker
/// still uses it.</summary>
public sealed class AiKeyRow
{
    public Guid Id { get; set; }

    /// <summary>SubjectPublicKeyInfo.</summary>
    public required byte[] PublicKey { get; set; }

    public required byte[] PrivateKey { get; set; }

    public required string Fingerprint { get; set; }

    /// <summary><c>active</c> or <c>retiring</c>.</summary>
    public required string Status { get; set; }

    public Instant CreatedAt { get; set; }
}

/// <summary>A job sent to the workers (<c>ai.job</c>, <c>docs/architecture.md</c> §9.3). <see cref="ResponseSecret"/> is
/// encrypted by Data Protection and erased once the result is fetched; the row is operational state, not a household
/// decision, and stays out of the audit log.</summary>
public sealed class JobRow
{
    public Guid Id { get; set; }

    public required string Type { get; set; }

    public int Version { get; set; }

    public required string Input { get; set; }

    public required string InputHash { get; set; }

    public string? FileRef { get; set; }

    public required string DataClass { get; set; }

    public Guid? TargetWorkerId { get; set; }

    /// <summary><c>queued</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>dead</c>.</summary>
    public required string Status { get; set; }

    public int Attempts { get; set; }

    public Instant RunAfter { get; set; }

    public Instant? NextPollAt { get; set; }

    public Instant? LeaseUntil { get; set; }

    public Guid? WorkerId { get; set; }

    public string? WorkerJobId { get; set; }

    public byte[]? ResponseSecret { get; set; }

    public string? Result { get; set; }

    public string? Error { get; set; }

    public string? WaitingReason { get; set; }

    public Instant CreatedAt { get; set; }

    public Instant? SentAt { get; set; }

    public Instant? FinishedAt { get; set; }

    public uint RowVersion { get; set; }
}
