using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>The admin status lists the failed sends of the last 30 days one page at a time, newest first.</summary>
public class AdminNotificationsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/admin/notifications";

    public sealed record FailureResponse(Guid Id);
    public sealed record FailurePageResponse(List<FailureResponse> Items, int Total, int Page);
    public sealed record ViewResponse(FailurePageResponse Failures);

    private static async Task<List<Guid>> SeedAsync(NidApp app)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var now = app.Services.GetRequiredService<IClock>().GetCurrentInstant();
        var ids = new List<Guid>();
        // 51 failures within the window, the two oldest sharing their instant, and one older than the window.
        for (var i = 0; i < 52; i++)
        {
            var id = Guid.CreateVersion7();
            var notification = new NotificationRow
            {
                Id = Guid.NewGuid(), PersonId = app.AdultB.Value, Kind = "tenancy_end", Key = $"tenancy_end:{i}", Level = "action",
                NotifyOn = new LocalDate(2025, 10, 1), State = "open", CreatedAt = now - NodaTime.Duration.FromDays(10),
            };
            db.Notifications.Add(notification);
            if (i < 51)
                ids.Add(id);
            db.Deliveries.Add(new NotificationDeliveryRow
            {
                Id = id, NotificationId = notification.Id, PersonId = app.AdultB.Value, Channel = "email", State = "failed", Attempts = 6,
                LastError = "notifications.channel_unreachable 421", Round = 1,
                CreatedAt = i == 51 ? now - NodaTime.Duration.FromDays(40) : now - NodaTime.Duration.FromDays(1) - NodaTime.Duration.FromMinutes(Math.Min(i, 49)),
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    [Fact]
    public async Task Failures_answer_one_page_and_the_total_of_the_window()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ids = await SeedAsync(app);

        var first = (await app.GetAsync<ViewResponse>(app.AdultA, Route)).Failures;
        var second = (await app.GetAsync<ViewResponse>(app.AdultA, $"{Route}?page=2")).Failures;

        Assert.Equal((50, 51, 1), (first.Items.Count, first.Total, first.Page));
        // The last two seeded failures share their instant: the lower id comes first, the other one opens the second page.
        Assert.Equal(ids.Take(49), first.Items.Take(49).Select(f => f.Id));
        Assert.Equal(ids.Skip(49).Order().First(), first.Items[49].Id);
        Assert.Equal((1, 51, 2), (second.Items.Count, second.Total, second.Page));
        Assert.Equal(ids.Skip(49).Order().Last(), Assert.Single(second.Items).Id);
    }

    [Fact]
    public async Task Failures_page_is_clamped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        Assert.Equal(1, (await app.GetAsync<ViewResponse>(app.AdultA, $"{Route}?page=0")).Failures.Page);
        Assert.Equal(1, (await app.GetAsync<ViewResponse>(app.AdultA, $"{Route}?page=-5")).Failures.Page);
        var past = (await app.GetAsync<ViewResponse>(app.AdultA, $"{Route}?page={int.MaxValue}")).Failures;
        Assert.Equal((0, 51), (past.Items.Count, past.Total));
    }
}
