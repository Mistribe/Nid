using Nid.Ai.Contracts;

namespace Nid.Ai.Domain;

/// <summary>What may happen to a worker in each state (<c>docs/ai/README.md</c> §3, §12): pending until paired, then
/// active or disabled, revoked for good. Each rule answers the refusal code, or <c>null</c> when allowed.</summary>
public static class WorkerRules
{
    /// <summary>How long a worker's previous key keeps opening requests after a rotation.</summary>
    public static readonly TimeSpan KeyOverlap = TimeSpan.FromHours(24);

    /// <summary>How long a registration waits for its pairing.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(1);

    /// <summary>A Nid key rotation a worker has not confirmed after this long is flagged on the screen.</summary>
    public static readonly TimeSpan RotationOverdue = TimeSpan.FromDays(7);

    public const int MaxNameLength = 60;

    public static string? Pair(WorkerStatus status)
        => status is WorkerStatus.Pending ? null : Settled(status, AiCodes.WorkerNotPending);

    public static string? Disable(WorkerStatus status) => Active(status);

    public static string? Call(WorkerStatus status) => Active(status);

    public static string? RotateKey(WorkerStatus status) => Active(status);

    public static string? Enable(WorkerStatus status)
        => status is WorkerStatus.Disabled ? null : Settled(status, AiCodes.WorkerNotDisabled);

    public static string? Revoke(WorkerStatus status) => status is WorkerStatus.Revoked ? AiCodes.WorkerRevoked : null;

    public static string? Rename(WorkerStatus status, string name) => Revoke(status) ?? Name(name);

    public static string? Name(string name)
        => name.Trim() is { Length: > 0 and <= MaxNameLength } ? null : AiCodes.InvalidName;

    /// <summary>A worker is reached at <c>https://host[:port]</c>: no path, query or credentials.</summary>
    public static string? Url(string url, out Uri? parsed)
    {
        parsed = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && uri.Scheme == Uri.UriSchemeHttps
                 && uri.UserInfo.Length == 0
                 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? new Uri($"https://{uri.IdnHost}:{uri.Port}")
            : null;
        return parsed is null ? AiCodes.InvalidUrl : null;
    }

    private static string? Active(WorkerStatus status)
        => status is WorkerStatus.Active ? null : Settled(status, AiCodes.WorkerNotActive);

    private static string Settled(WorkerStatus status, string otherwise)
        => status is WorkerStatus.Revoked ? AiCodes.WorkerRevoked : otherwise;
}
