using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Host;

[Collection("postgres")]
public class PreferencesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_new_session_carries_the_default_preferences()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var session = await app.GetAsync<JsonElement>(app.AdultA, "/auth/session");

        var preferences = session.GetProperty("preferences");
        Assert.Equal("neutre", preferences.GetProperty("theme").GetString());
        Assert.Equal("system", preferences.GetProperty("mode").GetString());
        Assert.False(preferences.GetProperty("chartPatterns").GetBoolean());
    }

    [Fact]
    public async Task A_new_session_carries_no_guide_seen()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var session = await app.GetAsync<JsonElement>(app.AdultA, "/auth/session");

        var guides = session.GetProperty("preferences").GetProperty("guidesSeen");
        Assert.Equal(JsonValueKind.Object, guides.ValueKind);
        Assert.Empty(guides.EnumerateObject());
    }

    [Fact]
    public async Task Guides_seen_are_saved_replaced_and_reset()
    {
        await using var app = await NidApp.StartAsync(fixture);
        async Task<JsonElement> Guides() => (await app.GetAsync<JsonElement>(app.AdultA, "/auth/session")).GetProperty("preferences").GetProperty("guidesSeen");

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { guidesSeen = new { dashboard = 1 } })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { guidesSeen = new { dashboard = 1, loans = 1 } })).EnsureSuccessStatusCode();
        var both = await Guides();
        Assert.Equal(1, both.GetProperty("dashboard").GetInt32());
        Assert.Equal(1, both.GetProperty("loans").GetInt32());

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { theme = "sauge" })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await Guides()).EnumerateObject().Count());

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { guidesSeen = new { } })).EnsureSuccessStatusCode();
        Assert.Empty((await Guides()).EnumerateObject());
    }

    [Fact]
    public async Task An_invalid_guide_is_refused_with_its_code()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences",
            new { guidesSeen = new Dictionary<string, int> { ["Loans"] = 1 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("core.preference_invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_change_is_merged_saved_and_read_back_by_the_next_session()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { theme = "sauge" });
        response.EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { mode = "dark" })).EnsureSuccessStatusCode();

        var preferences = (await app.GetAsync<JsonElement>(app.AdultA, "/auth/session")).GetProperty("preferences");
        Assert.Equal("sauge", preferences.GetProperty("theme").GetString());
        Assert.Equal("dark", preferences.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Each_adult_has_their_own()
    {
        await using var app = await NidApp.StartAsync(fixture);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { theme = "terre" })).EnsureSuccessStatusCode();

        var other = (await app.GetAsync<JsonElement>(app.AdultB, "/auth/session")).GetProperty("preferences");
        Assert.Equal("neutre", other.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Keys_this_endpoint_does_not_own_survive_a_change()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "update core.person set preferences = '{\"ai_cloud_default\":\"never\"}' where id = @id", connection);
            command.Parameters.AddWithValue("id", app.AdultA.Value);
            await command.ExecuteNonQueryAsync();
        }

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { theme = "ardoise", guidesSeen = new { loans = 2 } })).EnsureSuccessStatusCode();

        await using var read = new NpgsqlConnection(app.ConnectionString);
        await read.OpenAsync();
        await using var select = new NpgsqlCommand("select preferences::text from core.person where id = @id", read);
        select.Parameters.AddWithValue("id", app.AdultA.Value);
        using var stored = JsonDocument.Parse((string)(await select.ExecuteScalarAsync())!);
        Assert.Equal("never", stored.RootElement.GetProperty("ai_cloud_default").GetString());
        Assert.Equal("ardoise", stored.RootElement.GetProperty("theme").GetString());
        Assert.Equal(2, stored.RootElement.GetProperty("guides_seen").GetProperty("loans").GetInt32());
    }

    [Fact]
    public async Task An_invalid_value_is_refused_with_its_code()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { mode = "sepia" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("core.preference_invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_key_is_a_bad_request()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/core/me/preferences", new { colour = "red" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The body fails to bind before any endpoint filter runs, so MalformedInputFilter never sees it: the
        // answer is a bare 400 with no Problem body (the SPA's client gives it one carrying the status).
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }
}
