using System.Runtime.CompilerServices;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Host;

/// <summary>
/// Every test starts its own host: one a library keeps alive after disposal (a running timer, a static cache holding its
/// container) stays for the whole run, ~7 MB and a ticking timer each, and the suite slowed from 1 s to 3 s a test.
/// </summary>
public class HostLifetimeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_disposed_host_is_collected()
    {
        var hosts = new List<WeakReference>();
        for (var i = 0; i < 10; i++)
            hosts.Add(await StartAndDisposeAsync());

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // Two may stay, whatever the count: the process's first, which OpenTelemetry's self-diagnostics captures once, and
        // at times one more not yet released (seen, not traced). A leak keeps them all: 10 of 10 before the fix.
        Assert.True(hosts.Count(h => h.IsAlive) <= 2, $"{hosts.Count(h => h.IsAlive)} of {hosts.Count} disposed hosts are still alive");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> StartAndDisposeAsync()
    {
        await using var app = await NidApp.StartAsync(fixture);
        return new WeakReference(app.Services);
    }
}
