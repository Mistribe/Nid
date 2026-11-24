using Nid.Ai.Contracts;
using NodaTime;

namespace Nid.Ai.Domain;

/// <summary>A worker as the router weighs it. <paramref name="Serves"/>: its last declaration lists the type and version asked
/// (false without a readable declaration).</summary>
public sealed record RouteCandidate(
    Guid Id, WorkerStatus Status, WorkerPlacement Placement, bool Safe, Instant? LastSeenAt, bool Serves, int Load, int MaxConcurrent);

/// <summary>Eligible workers (<c>docs/ai/README.md</c> §4.1): active, seen in the last 3 minutes, declaring the type and
/// version, allowed the data (<see cref="DataPolicy"/>); local first, then the least loaded, then the most recently seen.
/// A target worker is the only route.</summary>
public static class WorkerRouting
{
    public static readonly Duration OnlineFor = Duration.FromMinutes(3);

    public static bool Online(Instant? lastSeenAt, Instant now) => lastSeenAt is { } seen && now - seen <= OnlineFor;

    /// <summary>
    /// The workers in the order to try, or the reason none may take it. <paramref name="householdAllows"/>: the household's
    /// opt-in to remote workers; <paramref name="offsite"/>: every adult the data concerns allows it to leave (decision M).
    /// </summary>
    public static (IReadOnlyList<Guid> Workers, string? Reason) Route(
        IEnumerable<RouteCandidate> candidates, Instant now, string dataClass, Guid? target, bool householdAllows, bool offsite)
    {
        var refusals = new List<string>();
        var eligible = new List<RouteCandidate>();
        foreach (var worker in candidates)
        {
            if (worker.Status != WorkerStatus.Active || !Online(worker.LastSeenAt, now))
                continue;
            if (target is { } only && worker.Id != only)
                continue;
            if (!worker.Serves)
                continue;
            if (DataPolicy.Refusal(worker.Placement, worker.Safe, dataClass, householdAllows && offsite) is { } refusal)
                refusals.Add(refusal);
            else
                eligible.Add(worker);
        }

        var ordered = eligible
            .OrderBy(w => w.Placement == WorkerPlacement.Local ? 0 : 1)
            .ThenBy(w => (double)w.Load / Math.Max(1, w.MaxConcurrent))
            .ThenByDescending(w => w.LastSeenAt)
            .Select(w => w.Id)
            .ToList();
        var reason = DataPolicy.Reason(refusals);
        // The household allows remote workers, so the person's own consent is what kept it home.
        if (reason == DataPolicy.RemoteNotAllowed && householdAllows)
            reason = JobCodes.OffsiteNotAllowed;
        return (ordered, ordered.Count == 0 ? reason ?? JobCodes.NoWorker : null);
    }
}
