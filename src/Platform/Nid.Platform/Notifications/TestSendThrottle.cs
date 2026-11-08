using System.Collections.Concurrent;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>One test message per (person, channel) every 30 seconds, so a repeated click cannot hammer a channel.</summary>
public sealed class TestSendThrottle(IClock clock)
{
    public static readonly NodaTime.Duration Interval = NodaTime.Duration.FromSeconds(30);

    private const int MaxRemembered = 10_000;
    private readonly ConcurrentDictionary<(Guid Person, string Channel), Instant> lastSent = new();

    /// <summary>True, and the slot taken, when the person's last test on the channel is at least <see cref="Interval"/> old.</summary>
    public bool TryTake(PersonId person, string channel)
    {
        var now = clock.GetCurrentInstant();
        if (lastSent.Count > MaxRemembered)
            lastSent.Clear(); // a bound, not an eviction policy: at worst one test goes through early
        var previous = lastSent.GetOrAdd((person.Value, channel), Instant.MinValue);
        return now - previous >= Interval && lastSent.TryUpdate((person.Value, channel), now, previous);
    }
}
