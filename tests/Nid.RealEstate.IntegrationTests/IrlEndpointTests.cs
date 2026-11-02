using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Rental spec §3.1: the admin routes of the IRL series, and the weekly job in the scheduler.</summary>
public class IrlEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sync = "/api/v1/realestate/irl/sync", List = "/api/v1/realestate/irl";

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
    public async Task The_three_routes_are_admin_only()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, List)).StatusCode);
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
        var value = listed.GetProperty("values")[0];
        Assert.Equal(("2025-Q2", "146.68", "manual", "2025-07-13"), (value.GetProperty("quarter").GetString(), value.GetProperty("value").GetString(),
            value.GetProperty("source").GetString(), value.GetProperty("publishedOn").GetString()));
        Assert.Equal(0, listed.GetProperty("runs").GetArrayLength());

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

    private sealed record Job(string Key, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, string State);
}
