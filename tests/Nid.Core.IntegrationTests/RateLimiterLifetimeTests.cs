using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Auth;

namespace Nid.Core.IntegrationTests;

public class RateLimiterLifetimeTests
{
    /// <summary>
    /// The token limiter is disposed with the provider that holds it. A live partitioned limiter's heartbeat timer
    /// kept every finished test host in memory, and ticking.
    /// </summary>
    [Fact]
    public async Task The_token_limiter_is_disposed_with_the_container()
    {
        var provider = new ServiceCollection().AddTokenRateLimiter().BuildServiceProvider();
        var limiter = provider.GetRequiredService<PartitionedRateLimiter<HttpContext>>();

        await provider.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire(new DefaultHttpContext()));
    }
}
