using NodaTime;

namespace Nid.Platform.Contracts;

/// <summary>
/// What one module's run log says about its last runs: <paramref name="LastFinishedFailed"/> when the latest finished run failed,
/// <paramref name="LastSucceededOn"/> the (UTC) day of the latest success. <paramref name="Source"/>: "fx", "catalog", "prices" or "irl".
/// </summary>
public sealed record SyncState(string Source, bool LastFinishedFailed, LocalDate? LastSucceededOn)
{
    /// <summary>From the runs, most recent first; <c>null</c> when none has finished (never ran, or running).</summary>
    public static SyncState? FromRuns(string source, IEnumerable<(bool Running, bool Succeeded, Instant At)> recentFirst)
    {
        var runs = recentFirst.ToList();
        var finished = runs.Where(r => !r.Running).ToList();
        if (finished.Count == 0)
            return null;
        var latest = finished[0];
        var success = runs.Where(r => r.Succeeded).Select(r => (Instant?)r.At).FirstOrDefault();
        return new SyncState(source, !latest.Succeeded, success?.InUtc().Date);
    }
}

/// <summary>A module's run log, read for the system health notices (notifications design §2.3). <c>null</c>: never ran, or running.</summary>
public interface ISyncHealth
{
    Task<SyncState?> ReadAsync(CancellationToken ct);
}
