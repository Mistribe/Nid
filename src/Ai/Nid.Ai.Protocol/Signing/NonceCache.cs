using System.Collections.Concurrent;

namespace Nid.Ai.Protocol.Signing;

/// <summary>The nonces a worker has seen in the last five minutes: a signed request sent twice is refused the second
/// time. Five minutes covers the ±60 s window with room; the worker keeps it in memory, so a restart forgets it, and the
/// window still bounds a replay to the minute after the original.</summary>
public sealed class NonceCache(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public bool TryUse(string nonce)
    {
        var now = time.GetUtcNow();
        Sweep(now);

        var accepted = true;
        _seen.AddOrUpdate(nonce, now, (_, seenAt) =>
        {
            if (now - seenAt < Lifetime)
            {
                accepted = false;
                return seenAt;
            }

            return now;
        });
        return accepted;
    }

    private void Sweep(DateTimeOffset now)
    {
        if (now < _nextSweep)
            return;

        _nextSweep = now + Lifetime;
        foreach (var (nonce, seenAt) in _seen)
            if (now - seenAt >= Lifetime)
                _seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(nonce, seenAt));
    }
}
