namespace Nid.Ai.Worker;

/// <summary>Where the worker keeps its state and how it is reached.</summary>
public sealed class WorkerOptions
{
    public string DataPath { get; set; } = "/data";

    public int Port { get; set; } = 8443;

    /// <summary>Comma-separated names the self-signed certificate is issued for.</summary>
    public string Hostnames { get; set; } = "worker,localhost";
}

/// <summary>Everything the worker keeps, in <c>state.json</c> on its volume: its keys, its certificate, the Nid key it
/// pinned, the pairing code. Never a credential that opens Nid. Keys are base64: the worker's HPKE private keys, Nid's
/// public keys as SPKI, the certificate as PKCS #12.</summary>
public sealed record WorkerState
{
    public required string WorkerKey { get; init; }

    public string? PreviousWorkerKey { get; init; }

    public DateTimeOffset? PreviousWorkerKeyUntil { get; init; }

    public required string TlsCertificate { get; init; }

    public string? NidKey { get; init; }

    public string? PreviousNidKey { get; init; }

    public DateTimeOffset? PreviousNidKeyUntil { get; init; }

    public string? PendingNidKey { get; init; }

    public string? PairingCode { get; init; }

    public DateTimeOffset? PairingCodeUntil { get; init; }

    public int PairingAttemptsLeft { get; init; }

    public bool IsPaired => NidKey is not null;
}
