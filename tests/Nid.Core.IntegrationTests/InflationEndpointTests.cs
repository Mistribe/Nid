using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests;

/// <summary>The inflation routes: any adult reads, the admin collects and enters values; the weekly job is in the scheduler.</summary>
public class InflationEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = "/api/v1/core/inflation";

    private sealed class AcceptingTrigger : ICpiJobTrigger
    {
        public Task<Result> TriggerAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("code").GetString();

    [Fact]
    public async Task Sync_is_202_then_409_while_running()
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<ICpiJobTrigger>(new AcceptingTrigger()));

        var accepted = await app.RawAsync(app.AdultA, HttpMethod.Post, Root + "/sync");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("requested").GetBoolean());

        await using (var scope = app.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ICpiRunLog>().TryStartAsync(CancellationToken.None));
        var running = await app.RawAsync(app.AdultA, HttpMethod.Post, Root + "/sync");
        Assert.Equal((HttpStatusCode.Conflict, "core.cpi_sync_running"), (running.StatusCode, await CodeAsync(running)));
    }

    [Fact]
    public async Task Admin_routes_are_403_for_a_non_admin()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, Root + "/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, Root + "/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, Root + "/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, Root + "/2026/9", new { value = "103.40" })).StatusCode);
    }

    [Fact]
    public async Task Get_inflation_is_open_to_any_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, Root)).StatusCode);
    }

    [Fact]
    public async Task Put_manual_value_round_trips()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, Root + "/2025/9", new { value = "103.40" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var value = JsonDocument.Parse(await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(("2025-09", "103.40", "manual"), (value.GetProperty("month").GetString(), value.GetProperty("value").GetString(), value.GetProperty("source").GetString()));
        Assert.Equal(JsonValueKind.Null, value.GetProperty("publishedOn").ValueKind);
        Assert.Equal("2025-10-15", value.GetProperty("fetchedOn").GetString());

        var listed = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Root + "/admin")).RootElement;
        Assert.Equal("2025-09", listed.GetProperty("items")[0].GetProperty("month").GetString());

        var bad = await app.RawAsync(app.AdultA, HttpMethod.Put, Root + "/2025/9", new { value = "abc" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    public async Task Sync_is_409_when_inflation_is_turned_off()
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<ICpiJobTrigger>(new AcceptingTrigger()));
        await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { inflationEnabled = false });

        var off = await app.RawAsync(app.AdultA, HttpMethod.Post, Root + "/sync");

        Assert.Equal((HttpStatusCode.Conflict, "core.cpi_disabled"), (off.StatusCode, await CodeAsync(off)));
    }

    [Fact]
    public async Task A_manual_value_for_a_month_from_insee_is_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Nid.Core.Domain.Inflation.ICpiStore>().UpsertAsync(
                new Nid.Core.Domain.Inflation.CpiValue(new NodaTime.YearMonth(2025, 9), 103.1m, new NodaTime.LocalDate(2025, 10, 10), new NodaTime.LocalDate(2025, 10, 11), "insee"),
                CancellationToken.None);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Root + "/2025/9", new { value = "103.40" });

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "core.cpi_from_insee"), (response.StatusCode, await CodeAsync(response)));
    }

    [Fact]
    public async Task A_put_without_a_body_is_a_malformed_request()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Root + "/2025/9");

        // Minimal APIs answer 400 themselves to a missing body (no Problem Details: the filter only sees what the handler throws).
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_weekly_job_is_scheduled_on_monday_seven_utc()
    {
        var name = "nid-test-" + Guid.NewGuid().ToString("N");
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["Jobs:Enabled"] = "true",
            ["Jobs:SchedulerName"] = name,
        });

        // The cron trigger itself: the job's own next run also counts the startup collection of an empty series.
        var scheduler = await app.Services.GetRequiredKeyedService<Quartz.ISchedulerFactory>(name).GetScheduler(TestContext.Current.CancellationToken);
        var trigger = await scheduler.GetTrigger(new Quartz.TriggerKey("cpi-weekly"), TestContext.Current.CancellationToken);
        var next = trigger!.NextFireTimeUtc!.Value.UtcDateTime;
        Assert.Equal((7, 0, DayOfWeek.Monday), (next.Hour, next.Minute, next.DayOfWeek));
    }

    public sealed record ValuePageResponse(List<ValueResponse> Items, int Total, int Page);
    public sealed record ValueResponse(string Month);
    public sealed record RunPageResponse(List<RunResponse> Items, int Total, int Page);
    public sealed record RunResponse(Guid Id);

    /// <summary>51 months from 2000-01 (the oldest is index 0).</summary>
    private static async Task SeedValuesAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        for (var i = 0; i < 51; i++)
            db.Cpi.Add(new CpiRow { Month = $"{2000 + i / 12}-{i % 12 + 1:00}", Value = 100m, FetchedOn = new LocalDate(2026, 1, 1), Source = "insee" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>51 finished runs a minute apart, the oldest (index 0) first; the last two share a start to pin the tie-break.</summary>
    private static async Task<List<Guid>> SeedRunsAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var start = Instant.FromUtc(2026, 1, 1, 0, 0);
        var ids = new List<Guid>();
        for (var i = 0; i < 51; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            db.CpiRuns.Add(new CpiRunRow { Id = id, StartedAt = start.Plus(NodaTime.Duration.FromMinutes(Math.Min(i, 49))), Status = "succeeded" });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    [Fact]
    public async Task Values_answer_one_page_newest_month_first_and_the_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedValuesAsync(app);

        var first = await app.GetAsync<ValuePageResponse>(app.AdultA, Root + "/admin");
        var second = await app.GetAsync<ValuePageResponse>(app.AdultA, Root + "/admin?page=2");

        Assert.Equal((50, 51, 1), (first.Items.Count, first.Total, first.Page));
        Assert.Equal(["2004-03", "2004-02"], first.Items.Take(2).Select(v => v.Month));
        Assert.Equal((1, 51, 2), (second.Items.Count, second.Total, second.Page));
        Assert.Equal("2000-01", Assert.Single(second.Items).Month);
    }

    [Fact]
    public async Task Values_page_is_clamped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedValuesAsync(app);

        Assert.Equal(1, (await app.GetAsync<ValuePageResponse>(app.AdultA, Root + "/admin?page=0")).Page);
        Assert.Equal(1, (await app.GetAsync<ValuePageResponse>(app.AdultA, Root + "/admin?page=-5")).Page);
        var past = await app.GetAsync<ValuePageResponse>(app.AdultA, $"{Root}/admin?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.Items.Count, past.Total));
    }

    [Fact]
    public async Task Runs_answer_one_page_and_the_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ids = await SeedRunsAsync(app);

        var first = await app.GetAsync<RunPageResponse>(app.AdultA, Root + "/runs");
        var second = await app.GetAsync<RunPageResponse>(app.AdultA, Root + "/runs?page=2");

        Assert.Equal((50, 51, 1), (first.Items.Count, first.Total, first.Page));
        // The last two seeded runs share a start: the higher id comes first, the oldest run comes last.
        Assert.Equal(ids.Skip(49).OrderDescending(), first.Items.Take(2).Select(r => r.Id));
        Assert.Equal((1, 51, 2), (second.Items.Count, second.Total, second.Page));
        Assert.Equal(ids[0], Assert.Single(second.Items).Id);
    }

    [Fact]
    public async Task Runs_page_is_clamped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedRunsAsync(app);

        Assert.Equal(1, (await app.GetAsync<RunPageResponse>(app.AdultA, Root + "/runs?page=0")).Page);
        Assert.Equal(1, (await app.GetAsync<RunPageResponse>(app.AdultA, Root + "/runs?page=-5")).Page);
        var past = await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.Items.Count, past.Total));
    }
}
