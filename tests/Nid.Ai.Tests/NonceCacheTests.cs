using Nid.Ai.Protocol.Signing;

namespace Nid.Ai.Tests;

public sealed class NonceCacheTests
{
    [Fact]
    public void A_nonce_is_accepted_once()
    {
        var nonces = new NonceCache(new Clock());

        Assert.True(nonces.TryUse("n1"));
        Assert.False(nonces.TryUse("n1"));
        Assert.True(nonces.TryUse("n2"));
    }

    [Fact]
    public void A_nonce_is_forgotten_after_five_minutes()
    {
        var clock = new Clock();
        var nonces = new NonceCache(clock);
        Assert.True(nonces.TryUse("n1"));

        clock.Now += TimeSpan.FromMinutes(4);
        Assert.False(nonces.TryUse("n1"));

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.True(nonces.TryUse("n1"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
