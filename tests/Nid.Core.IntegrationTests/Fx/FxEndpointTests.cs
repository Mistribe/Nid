using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using NodaTime;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.Core.IntegrationTests.Fx;

public class FxEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = "/api/v1/core/fx-rates";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record RateResponse(string OnDate, string Quote, string Rate, string Source);
    public sealed record RunResponse(Guid Id, string Kind, string Status, string? FailureCode);
    public sealed record RunPageResponse(List<RunResponse> Items, int Total, int Page);

    private sealed class OkTrigger : IFxJobTrigger
    {
        public Task<Result> TriggerBackfillAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();

    [Fact]
    public async Task A_manual_rate_is_put_then_listed()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Root}/2025-10-01/EUR", new { rate = "0.8612" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var listed = await app.GetAsync<List<RateResponse>>(app.AdultB, $"{Root}?quote=EUR&from=2025-09-01&to=2025-10-31");
        Assert.Equal(new RateResponse("2025-10-01", "EUR", "0.8612", "manual"), Assert.Single(listed));
    }

    [Fact]
    public async Task An_invalid_rate_is_400_and_a_rate_as_a_number_is_400()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var invalid = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Root}/2025-10-01/EUR", new { rate = "-1" });
        Assert.Equal((HttpStatusCode.BadRequest, FxCodes.InvalidRate), (invalid.StatusCode, await CodeOf(invalid)));

        var number = await app.RawTextAsync(app.AdultA, HttpMethod.Put, $"{Root}/2025-10-01/EUR", """{"rate":0.8}""");
        Assert.Equal(HttpStatusCode.BadRequest, number.StatusCode);
    }

    [Fact]
    public async Task A_range_over_400_days_is_400()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?quote=EUR&from=2024-01-01&to=2025-10-01");
        Assert.Equal((HttpStatusCode.BadRequest, FxCodes.InvalidRange), (response.StatusCode, await CodeOf(response)));
    }

    [Fact]
    public async Task Sync_is_202_then_409_while_a_run_is_in_progress()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddSingleton<IFxJobTrigger>(new OkTrigger()));

        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/sync")).StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRunLog>().TryStartAsync(FxRunKind.Daily, TestContext.Current.CancellationToken);
        var busy = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/sync");
        Assert.Equal((HttpStatusCode.Conflict, FxCodes.SyncRunning), (busy.StatusCode, await CodeOf(busy)));
    }

    /// <summary>51 finished runs a minute apart, the oldest (index 0) first in the table; the last two share a start to pin the tie-break.</summary>
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
            db.FxSyncRuns.Add(new FxSyncRunRow { Id = id, Kind = "daily", StartedAt = start.Plus(NodaTime.Duration.FromMinutes(Math.Min(i, 49))), Status = "succeeded" });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    [Fact]
    public async Task Runs_answer_one_page_and_the_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ids = await SeedRunsAsync(app);

        var first = await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs");
        var second = await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs?page=2");

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

        Assert.Equal(1, (await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs?page=0")).Page);
        Assert.Equal(1, (await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs?page=-5")).Page);
        var past = await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs?page={int.MaxValue}");
        Assert.Equal((0, 51), (past.Items.Count, past.Total));
    }

    [Fact]
    public async Task Runs_are_listed_newest_first()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.InvokeAsync(app.AdultA, new Nid.Core.Application.Fx.BackfillFxRatesCommand(), TestContext.Current.CancellationToken);

        var runs = (await app.GetAsync<RunPageResponse>(app.AdultA, $"{Root}/runs")).Items;

        Assert.Equal(("backfill", "failed", FxCodes.SourceUnavailable), (runs[0].Kind, runs[0].Status, runs[0].FailureCode));
    }

    [Fact]
    public async Task Every_endpoint_requires_a_session()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawAnonymousAsync(HttpMethod.Get, $"{Root}/runs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rate_writes_and_the_run_journal_are_refused_to_a_non_admin_but_rates_stay_readable()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Put, $"{Root}/2025-10-01/EUR", new { rate = "0.86" }),
                     (HttpMethod.Post, $"{Root}/sync", null),
                     (HttpMethod.Get, $"{Root}/runs", null),
                 })
        {
            var response = await app.RawAsync(app.AdultB, method, url, body);
            Assert.Equal((HttpStatusCode.Forbidden, AdminCodes.Required), (response.StatusCode, await CodeOf(response)));
        }

        var read = await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}?quote=EUR&from=2025-09-01&to=2025-10-31");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
