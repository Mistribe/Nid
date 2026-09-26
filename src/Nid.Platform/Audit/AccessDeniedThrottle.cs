using System.Collections.Concurrent;
using NodaTime;

namespace Nid.Platform.Audit;

/// <summary>Spec U12: one <c>access_denied</c> per (actor, item, minute), so a looping client cannot flood the log.</summary>
public sealed class AccessDeniedThrottle(IClock clock)
{
    private const int MaxRemembered = 10_000;
    private readonly ConcurrentDictionary<(Guid Actor, Guid Item), long> lastMinute = new();

    public bool ShouldRecord(Guid actor, Guid item)
    {
        var minute = clock.GetCurrentInstant().ToUnixTimeSeconds() / 60;
        if (lastMinute.Count > MaxRemembered)
            lastMinute.Clear(); // a bound, not an eviction policy: at worst one refusal is recorded twice
        var previous = lastMinute.GetOrAdd((actor, item), -1);
        return previous != minute && lastMinute.TryUpdate((actor, item), minute, previous);
    }
}
