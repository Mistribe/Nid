using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Goals.IntegrationTests;

/// <summary>#23: a goal created by mistake is deleted after a preview, with its funding links and progress entries.</summary>
public class GoalDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Goals = "/api/v1/goals";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, ItemId goal)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Goals}/{goal.Value}/deletion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    [Fact]
    public async Task Preview_lists_funding_and_progress()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var funded = await app.NewGoalAsync("Voyage", app.AdultA, funding: [(envelope, 5000)]);
        var free = await app.NewGoalAsync("Vélo", app.AdultA);
        foreach (var day in new[] { "2025-10-01", "2025-10-02" })
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{free.Value}/progress/{day}", new { amount = "100" })).EnsureSuccessStatusCode();

        var fundedPreview = await PreviewAsync(app, app.AdultA, funded);
        var freePreview = await PreviewAsync(app, app.AdultA, free);

        Assert.Equal("Voyage", fundedPreview.GetProperty("name").GetString());
        Assert.Equal(1, Removed(fundedPreview, "funding"));
        Assert.Equal(2, Removed(freePreview, "progress_entries"));
    }

    [Fact]
    public async Task Delete_removes_the_goal_and_keeps_its_envelopes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA, funding: [(envelope, 5000)]);
        var version = (await PreviewAsync(app, app.AdultA, goal)).GetProperty("version").GetUInt32();

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Goals}/{goal.Value}?version={version}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Goals}/{goal.Value}")).StatusCode);
        Assert.DoesNotContain((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Goals))).GetProperty("items").EnumerateArray(),
            g => g.GetProperty("id").GetGuid() == goal.Value);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{envelope.Value}")).StatusCode);
        await using var db = PostgresFixture.NewGoalsContext(app.ConnectionString);
        Assert.Empty(db.Funding.Where(f => f.GoalId == goal.Value));
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        var version = (await PreviewAsync(app, app.AdultA, goal)).GetProperty("version").GetUInt32();
        var goalVersion = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Goals}/{goal.Value}"))).GetProperty("version").GetUInt32();
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Goals}/{goal.Value}",
            new { name = "Japon", targetAmount = "10000", note = "x", funding = Array.Empty<object>(), version = goalVersion })).EnsureSuccessStatusCode();

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Goals}/{goal.Value}?version={version}"),
            HttpStatusCode.Conflict, ItemCodes.Changed);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Goals}/{goal.Value}")).StatusCode);
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Goals}/{goal.Value}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewGoalAsync("Voyage", app.AdultA);
        var secret = await app.NewGoalAsync("Cadeau", app.AdultA, Visibility.Private);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Goals}/{shared.Value}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Goals}/{shared.Value}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Goals}/{secret.Value}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Goals}/{secret.Value}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA);
        var version = (await PreviewAsync(app, app.AdultA, goal)).GetProperty("version").GetUInt32();
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        await AssertRefusedAsync(await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{Goals}/{goal.Value}?version={version}"),
            HttpStatusCode.Forbidden, BrowserOnlyCodes.Required);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Goals}/{goal.Value}")).StatusCode);
    }

    [Fact]
    public async Task Another_modules_item_answers_404_and_stays()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Goals}/{envelope.Value}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Goals}/{envelope.Value}?version=1")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{envelope.Value}")).StatusCode);
    }
}
