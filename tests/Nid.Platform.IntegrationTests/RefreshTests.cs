using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Contracts;
using Nid.Host.Jobs;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §3.3: the refresh queue and its throttle, without Quartz.</summary>
public class RefreshTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Start = Instant.FromUtc(2025, 10, 15, 4, 10);

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private sealed class OneSignalSource : INotificationSource
    {
        private static readonly NotificationKind Kind = new("fake_undated", "fake", NotificationLevel.Info, "fake", null);

        public string Name => "fake";

        public IReadOnlyList<NotificationKind> Kinds { get; } = [Kind];

        public Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyList<Signal>>([]));
    }

    [Fact]
    public async Task Jobs_disabled_means_a_no_op_refresh()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.IsType<NoNotificationRefresh>(app.Services.GetRequiredService<INotificationRefresh>());
    }

    [Fact]
    public async Task The_worker_drops_a_request_inside_the_throttle()
    {
        var clock = new SettableClock(Start);
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<INotificationSource>(); // only the fake one speaks here
            services.AddSingleton<INotificationSource>(new OneSignalSource());
        });
        var queue = new NotificationRefreshQueue();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:RefreshThrottleSeconds"] = "3600" }).Build();
        using var worker = new NotificationRefreshWorker(
            queue, app.Services.GetRequiredService<IServiceScopeFactory>(), clock, config, NullLogger<NotificationRefreshWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            queue.Request(app.AdultA);
            Assert.Equal(Start, await WaitForRanAtAsync(app, app.AdultA, Start));

            // 1000 s later: inside 3600 s (and past the 900 s default, so a wrong config key shows).
            clock.Now = Start + NodaTime.Duration.FromSeconds(1000);
            queue.Request(app.AdultA);
            await Task.Delay(500);
            Assert.Equal(Start, await RanAtAsync(app, app.AdultA));

            // Past the throttle: evaluated again.
            clock.Now = Start + NodaTime.Duration.FromSeconds(3700);
            queue.Request(app.AdultA);
            Assert.Equal(clock.Now, await WaitForRanAtAsync(app, app.AdultA, clock.Now));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Instant?> RanAtAsync(NidApp app, PersonId person)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.NotificationRuns.AsNoTracking().Where(r => r.PersonId == person.Value).Select(r => (Instant?)r.RanAt).SingleOrDefaultAsync();
    }

    private static async Task<Instant?> WaitForRanAtAsync(NidApp app, PersonId person, Instant expected)
    {
        for (var i = 0; i < 100; i++)
        {
            var ranAt = await RanAtAsync(app, person);
            if (ranAt == expected)
                return ranAt;
            await Task.Delay(100);
        }

        return await RanAtAsync(app, person);
    }
}
