using Nid.Ai.Contracts;

namespace Nid.Ai.Domain;

/// <summary>Which workers may see which data (<c>docs/ai/README.md</c> §6.1), before each adult's own consent (§6.3, #167).
/// A remote worker sees the data while it handles it: it counts as leaving the premises and receives nothing before the
/// household's opt-in. Local-only data goes to a <c>safe</c> worker only, whatever its placement.</summary>
public static class DataPolicy
{
    public const string RemoteNotAllowed = "ai.remote_not_allowed";
    public const string NoSafeWorker = "ai.no_safe_worker";

    /// <summary>Null when the worker may see this class; otherwise why not.</summary>
    public static string? Refusal(WorkerPlacement placement, bool safe, string dataClass, bool remoteAllowed)
    {
        if (dataClass == DataClasses.None)
            return null;
        if (dataClass == DataClasses.LocalOnly && !safe)
            return NoSafeWorker;
        return placement == WorkerPlacement.Remote && !remoteAllowed ? RemoteNotAllowed : null;
    }

    /// <summary>The reason to show when every worker was refused: the one that tells what to change. A missing safe worker
    /// first (no opt-in would open a non-safe one), then the opt-in.</summary>
    public static string? Reason(IReadOnlyCollection<string> refusals)
        => refusals.Contains(NoSafeWorker) ? NoSafeWorker : refusals.Contains(RemoteNotAllowed) ? RemoteNotAllowed : null;
}
