using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
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
        Assert.Equal("2025-09", listed.GetProperty("values")[0].GetProperty("month").GetString());

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

}
