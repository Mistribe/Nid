using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Goals.IntegrationTests;

/// <summary>The explorer's goal series: a free goal's readings, with the item rights of a goal; a funded goal has none.</summary>
public class GoalSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement[] Items(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().ToArray();

    private static bool Has(JsonElement catalogue, ItemId id) => Items(catalogue).Any(i => i.GetProperty("key").GetString() == $"goal:{id.Value}");

    private static async Task ReadAsync(NidApp app, PersonId actor, ItemId goal, string on, string amount)
        => (await app.RawAsync(actor, HttpMethod.Put, $"/api/v1/goals/{goal.Value}/progress/{on}", new { amount })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Catalogue_lists_a_shared_free_goal_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewGoalAsync("Vacances", app.AdultA);

        var found = Items(await GetAsync(app, app.AdultB, "/api/v1/series?q=vacances")).Single(i => i.GetProperty("key").GetString() == $"goal:{id.Value}");

        Assert.Equal((id.Value.ToString(), "currency:EUR", "goal"),
            (found.GetProperty("item").GetString(), found.GetProperty("unit").GetString(), found.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_funded_goal_has_no_series()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var hidden = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);
        var funded = await app.NewGoalAsync("Financé", app.AdultA, funding: [(shared, 10_000)]);
        var partial = await app.NewGoalAsync("Partiel", app.AdultA, funding: [(hidden, 10_000)]);

        var asA = await GetAsync(app, app.AdultA, "/api/v1/series?scope=me");
        Assert.False(Has(asA, funded));
        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=goal:{funded.Value}&scope=me", HttpStatusCode.NotFound);

        // Bob sees the shared goal but not its private envelope: the page says partial, so it is no free goal.
        Assert.False(Has(await GetAsync(app, app.AdultB, "/api/v1/series"), partial));
        await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=goal:{partial.Value}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_adults_private_goal_is_absent_and_its_points_are_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewGoalAsync("Vacances", app.AdultA, Visibility.Private);

        Assert.False(Has(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me"), id));
        var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=goal:{id.Value}", HttpStatusCode.NotFound);
        Assert.Equal(("core.series_not_found", $"goal:{id.Value}"), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task The_readers_own_private_goal_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewGoalAsync("Vacances", app.AdultA, Visibility.Private);

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=goal:{id.Value}&scope=household");
    }

    [Fact]
    public async Task A_free_goal_series_is_its_readings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewGoalAsync("Vacances", app.AdultA);
        await ReadAsync(app, app.AdultA, id, "2025-09-30", "1500");
        await ReadAsync(app, app.AdultA, id, "2025-06-30", "1000");

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=goal:{id.Value}")).GetProperty("series")[0];

        var points = series.GetProperty("points").EnumerateArray()
            .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal([("2025-06-30", 1000m), ("2025-09-30", 1500m)], points);
        var entries = (await GetAsync(app, app.AdultA, $"/api/v1/goals/{id.Value}")).GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("on").GetString()!, decimal.Parse(e.GetProperty("amount").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture)))
            .OrderBy(e => e.Item1).ToArray();
        Assert.Equal(entries, points);
        Assert.Equal(("currency:EUR", id.Value.ToString()), (series.GetProperty("unit").GetString(), series.GetProperty("item").GetString()));
    }

    [Theory]
    [InlineData("goal:nope")]
    [InlineData("envelope")]
    public async Task Malformed_or_foreign_ids_are_404(string key)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var s = key == "envelope" ? $"goal:{envelope.Value}" : key;

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s={s}", HttpStatusCode.NotFound);
    }
}
