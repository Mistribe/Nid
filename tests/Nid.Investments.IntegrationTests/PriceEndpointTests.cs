using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Investments.Contracts;
using Nid.Investments.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.IntegrationTests;

/// <summary>Holding prices spec §5: the admin routes of the collection, and the nightly job in the scheduler.</summary>
public class PriceEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sync = "/api/v1/investments/prices/sync", Runs = "/api/v1/investments/prices/runs";

    private sealed class AcceptingTrigger : IPriceJobTrigger
    {
        public Task<Result> TriggerAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    private static Task EnableAsync(NidApp app)
        => app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { pricesEnabled = true });

    [Fact]
    public async Task A_sync_request_is_accepted_when_prices_are_on_and_nothing_runs()
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<IPriceJobTrigger>(new AcceptingTrigger()));
        await EnableAsync(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Sync);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("requested").GetBoolean());
    }

    [Fact]
    public async Task A_sync_request_is_refused_while_the_feature_is_off()
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<IPriceJobTrigger>(new AcceptingTrigger()));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Sync);

        Assert.Equal((HttpStatusCode.Conflict, "investments.prices_disabled"), (response.StatusCode, await CodeAsync(response)));
    }

    [Fact]
    public async Task A_sync_request_is_refused_while_a_collection_runs()
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<IPriceJobTrigger>(new AcceptingTrigger()));
        await EnableAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IPriceRunLog>().TryStartAsync(CancellationToken.None));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Sync);

        Assert.Equal((HttpStatusCode.Conflict, "investments.price_sync_running"), (response.StatusCode, await CodeAsync(response)));
    }

    /// <summary>51 finished runs a minute apart; the last two share a start to pin the tie-break. Ids in seeding order, oldest first.</summary>
    private static async Task<List<Guid>> SeedRunsAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvestmentsDbContext>();
        var start = Instant.FromUtc(2026, 1, 1, 0, 0);
        var ids = new List<Guid>();
        for (var i = 0; i < 51; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            db.PriceRuns.Add(new PriceRunRow { Id = id, StartedAt = start.Plus(Duration.FromMinutes(Math.Min(i, 49))), Status = "succeeded" });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    private static async Task<JsonElement> PageAsync(NidApp app, string query = "")
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Runs + query)).RootElement;

    [Fact]
    public async Task Runs_answer_one_page_and_the_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ids = await SeedRunsAsync(app);

        var first = await PageAsync(app);
        var second = await PageAsync(app, "?page=2");

        Assert.Equal((50, 51, 1), (first.GetProperty("items").GetArrayLength(), first.GetProperty("total").GetInt32(), first.GetProperty("page").GetInt32()));
        // The last two seeded runs share a start: the higher id comes first, the oldest run comes last.
        Assert.Equal(ids.Skip(49).OrderDescending(), first.GetProperty("items").EnumerateArray().Take(2).Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal((1, 2), (second.GetProperty("items").GetArrayLength(), second.GetProperty("page").GetInt32()));
        Assert.Equal(ids[0], second.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Runs_page_is_clamped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedRunsAsync(app);

        Assert.Equal(1, (await PageAsync(app, "?page=0")).GetProperty("page").GetInt32());
        Assert.Equal(1, (await PageAsync(app, "?page=-5")).GetProperty("page").GetInt32());
        var past = await PageAsync(app, $"?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.GetProperty("items").GetArrayLength(), past.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task The_runs_list_shows_counts_and_both_routes_are_admin_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IPriceRunLog>().TryStartAsync(CancellationToken.None));

        var runs = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Runs)).RootElement;

        var items = runs.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("running", items[0].GetProperty("status").GetString());
        Assert.Equal(0, items[0].GetProperty("queried").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, Runs)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, Sync)).StatusCode);
    }

    [Fact]
    public async Task With_jobs_enabled_the_nightly_collection_fires_on_weekdays_at_21_UTC()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["Jobs:Enabled"] = "true",
            ["Jobs:SchedulerName"] = "nid-test-" + Guid.NewGuid().ToString("N"),
        });

        var jobs = await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs");

        var next = jobs.Single(j => j.Key == "prices-daily").NextRunAt!.Value.UtcDateTime;
        Assert.Equal((21, 0, true), (next.Hour, next.Minute, next.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday));
    }

    private sealed record Job(string Key, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, string State);
}
