using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Fx;

[Collection("postgres")]
public class FxEndpointTests(PostgresFixture fixture)
{
    private const string Root = "/api/v1/core/fx-rates";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record RateResponse(string OnDate, string Quote, string Rate, string Source);
    public sealed record RunResponse(Guid Id, string Kind, string Status, string? FailureCode);

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
            await scope.ServiceProvider.GetRequiredService<IFxRunLog>().TryStartAsync(FxRunKind.Daily, default);
        var busy = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/sync");
        Assert.Equal((HttpStatusCode.Conflict, FxCodes.SyncRunning), (busy.StatusCode, await CodeOf(busy)));
    }

    [Fact]
    public async Task Runs_are_listed_newest_first()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.InvokeAsync(app.AdultA, new Nid.Core.Application.Fx.BackfillFxRatesCommand());

        var runs = await app.GetAsync<List<RunResponse>>(app.AdultA, $"{Root}/runs");

        Assert.Equal(("backfill", "failed", FxCodes.SourceUnavailable), (runs[0].Kind, runs[0].Status, runs[0].FailureCode));
    }

    [Fact]
    public async Task Every_endpoint_requires_a_session()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var response = await app.RawAnonymousAsync(HttpMethod.Get, $"{Root}/runs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
