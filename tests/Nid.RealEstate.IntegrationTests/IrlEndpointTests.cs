using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Rental spec §3.1: the admin routes of the IRL series, and the weekly job in the scheduler.</summary>
public class IrlEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sync = "/api/v1/realestate/irl/sync", List = "/api/v1/realestate/irl", Runs = "/api/v1/realestate/irl/runs";

    private sealed class AcceptingTrigger : IIrlJobTrigger
    {
        public Task<Result> TriggerAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    private static Task EnableAsync(NidApp app)
        => app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { irlEnabled = true });

    [Fact]
    public async Task A_sync_request_is_accepted_when_irl_is_on_and_refused_while_off_or_running()
    {
        await using var app = await NidApp.StartAsync(fixture, s => s.AddSingleton<IIrlJobTrigger>(new AcceptingTrigger()));

        var off = await app.RawAsync(app.AdultA, HttpMethod.Post, Sync);
        Assert.Equal((HttpStatusCode.Conflict, "realestate.irl_disabled"), (off.StatusCode, await CodeAsync(off)));

        await EnableAsync(app);
        var accepted = await app.RawAsync(app.AdultA, HttpMethod.Post, Sync);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("requested").GetBoolean());

        await using (var scope = app.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IIrlRunLog>().TryStartAsync(CancellationToken.None));
        var running = await app.RawAsync(app.AdultA, HttpMethod.Post, Sync);
        Assert.Equal((HttpStatusCode.Conflict, "realestate.irl_sync_running"), (running.StatusCode, await CodeAsync(running)));
    }

    [Fact]
    public async Task The_admin_routes_are_admin_only()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, List)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, Runs)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, Sync)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, List + "/2026/2", new { value = "148.37", publishedOn = "2026-07-12" })).StatusCode);
    }

    [Fact]
    public async Task A_manual_value_is_put_listed_and_a_bad_one_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, List + "/2025/2", new { value = "146.68", publishedOn = "2025-07-13" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var listed = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, List)).RootElement;
        var value = listed.GetProperty("items")[0];
        Assert.Equal(("2025-Q2", "146.68", "manual", "2025-07-13"), (value.GetProperty("quarter").GetString(), value.GetProperty("value").GetString(),
            value.GetProperty("source").GetString(), value.GetProperty("publishedOn").GetString()));
        Assert.Equal(0, JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Runs)).RootElement.GetProperty("items").GetArrayLength());

        var invalid = await app.RawAsync(app.AdultA, HttpMethod.Put, List + "/2025/5", new { value = "146.68", publishedOn = "2025-07-13" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_irl_value"), (invalid.StatusCode, await CodeAsync(invalid)));
        var notANumber = await app.RawAsync(app.AdultA, HttpMethod.Put, List + "/2025/2", new { value = "abc", publishedOn = "2025-07-13" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notANumber.StatusCode);
        var noDate = await app.RawAsync(app.AdultA, HttpMethod.Put, List + "/2025/2", new { value = "146.68" });
        Assert.Equal("publishedOn", JsonDocument.Parse(await noDate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_manual_value_for_a_quarter_from_insee_is_unprocessable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Nid.RealEstate.Domain.IIrlStore>().UpsertAsync(
                new Nid.RealEstate.Domain.IrlValue(new Nid.RealEstate.Domain.IrlQuarter(2025, 2), 146.68m, new NodaTime.LocalDate(2025, 7, 13), new NodaTime.LocalDate(2025, 7, 14), "insee"),
                CancellationToken.None);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, List + "/2025/2", new { value = "150", publishedOn = "2025-07-13" });

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.irl_from_insee"), (response.StatusCode, await CodeAsync(response)));
    }

    [Fact]
    public async Task With_jobs_enabled_the_weekly_collection_fires_on_monday_at_07_UTC()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["Jobs:Enabled"] = "true",
            ["Jobs:SchedulerName"] = "nid-test-" + Guid.NewGuid().ToString("N"),
        });

        var jobs = await app.GetAsync<List<Job>>(app.AdultA, "/api/v1/admin/jobs");

        var next = jobs.Single(j => j.Key == "irl-weekly").NextRunAt!.Value.UtcDateTime;
        Assert.Equal((7, 0, DayOfWeek.Monday), (next.Hour, next.Minute, next.DayOfWeek));
    }

    public sealed record ValuePageResponse(List<ValueResponse> Items, int Total, int Page);
    public sealed record ValueResponse(string Quarter);
    public sealed record RunPageResponse(List<RunResponse> Items, int Total, int Page);
    public sealed record RunResponse(Guid Id);

    /// <summary>51 quarters from 1990-Q1 (the oldest is index 0).</summary>
    private static async Task SeedValuesAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();
        for (var i = 0; i < 51; i++)
            db.Irl.Add(new IrlRow { Quarter = $"{1990 + i / 4}-Q{i % 4 + 1}", Value = 100m, FetchedOn = new LocalDate(2026, 1, 1), Source = "insee" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>51 finished runs a minute apart, the oldest (index 0) first; the last two share a start to pin the tie-break.</summary>
    private static async Task<List<Guid>> SeedRunsAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();
        var start = Instant.FromUtc(2026, 1, 1, 0, 0);
        var ids = new List<Guid>();
        for (var i = 0; i < 51; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            db.IrlRuns.Add(new IrlRunRow { Id = id, StartedAt = start.Plus(Duration.FromMinutes(Math.Min(i, 49))), Status = "succeeded" });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    [Fact]
    public async Task Values_answer_one_page_newest_quarter_first_and_the_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedValuesAsync(app);

        var first = await app.GetAsync<ValuePageResponse>(app.AdultA, List);
        var second = await app.GetAsync<ValuePageResponse>(app.AdultA, List + "?page=2");

        Assert.Equal((50, 51, 1), (first.Items.Count, first.Total, first.Page));
        Assert.Equal(["2002-Q3", "2002-Q2"], first.Items.Take(2).Select(v => v.Quarter));
        Assert.Equal((1, 51, 2), (second.Items.Count, second.Total, second.Page));
        Assert.Equal("1990-Q1", Assert.Single(second.Items).Quarter);
    }

    [Fact]
    public async Task Values_page_is_clamped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedValuesAsync(app);

        Assert.Equal(1, (await app.GetAsync<ValuePageResponse>(app.AdultA, List + "?page=0")).Page);
        Assert.Equal(1, (await app.GetAsync<ValuePageResponse>(app.AdultA, List + "?page=-5")).Page);
        var past = await app.GetAsync<ValuePageResponse>(app.AdultA, $"{List}?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.Items.Count, past.Total));
    }

    [Fact]
    public async Task Runs_answer_one_page_and_the_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ids = await SeedRunsAsync(app);

        var first = await app.GetAsync<RunPageResponse>(app.AdultA, Runs);
        var second = await app.GetAsync<RunPageResponse>(app.AdultA, Runs + "?page=2");

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

        Assert.Equal(1, (await app.GetAsync<RunPageResponse>(app.AdultA, Runs + "?page=0")).Page);
        Assert.Equal(1, (await app.GetAsync<RunPageResponse>(app.AdultA, Runs + "?page=-5")).Page);
        var past = await app.GetAsync<RunPageResponse>(app.AdultA, $"{Runs}?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.Items.Count, past.Total));
    }

    private sealed record Job(string Key, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, string State);
}
