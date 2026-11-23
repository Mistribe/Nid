using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#533 (<c>docs/ai/README.md</c> §6.2, §6.3): each adult turns the assistant on for themselves; towards the cloud,
/// every adult is told; each sets their preference for their data off the premises.</summary>
public sealed class AssistantActivationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Me = "/api/v1/ai/assistant/me";

    [Fact]
    public async Task The_assistant_is_off_until_its_adult_turns_it_on()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var before = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/assistant/turn", new { question = "Bonjour ?" });
        await AssistantMe.EnableAsync(app, app.AdultA);

        Assert.Contains("ai.assistant_off", await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True((await ViewAsync(app, app.AdultA)).GetProperty("enabled").GetBoolean());
        Assert.False((await ViewAsync(app, app.AdultB)).GetProperty("enabled").GetBoolean()); // for oneself only
        Assert.Equal(["true"], await EventsAsync(app, "ai_assistant_changed", "enabled"));
    }

    [Fact]
    public async Task Turning_it_on_towards_the_cloud_tells_every_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "self_hosted_then_cloud" })).StatusCode);
        var before = await NoticesAsync(app);

        Assert.True((await ViewAsync(app, app.AdultB)).GetProperty("cloudPossible").GetBoolean());
        await AssistantMe.EnableAsync(app, app.AdultB);
        await AssistantMe.EnableAsync(app, app.AdultB); // unchanged: no second notice

        Assert.Equal(2, await NoticesAsync(app) - before); // one per adult
    }

    [Fact]
    public async Task Each_adult_sets_their_own_preference_for_their_data_off_the_premises()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssistantMe.OffsiteAsync(app, app.AdultB, "never");
        var invalid = await app.RawAsync(app.AdultB, HttpMethod.Put, $"{Me}/offsite", new { preference = "sometimes" });

        Assert.Equal("never", (await ViewAsync(app, app.AdultB)).GetProperty("offsiteDefault").GetString());
        Assert.Equal("ask", (await ViewAsync(app, app.AdultA)).GetProperty("offsiteDefault").GetString());
        Assert.Contains("ai.invalid_offsite_preference", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["never"], await EventsAsync(app, "ai_offsite_default_changed", "preference"));
    }

    private static async Task<JsonElement> ViewAsync(NidApp app, Core.Contracts.PersonId person)
        => await (await app.RawAsync(person, HttpMethod.Get, Me)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<int> NoticesAsync(NidApp app)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.Notifications.CountAsync(n => n.Kind == "ai.data_policy_changed", TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> EventsAsync(NidApp app, string name, string key)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return [.. (await platform.AuditLog.Where(r => r.Category == "security" && r.Action == name).OrderBy(r => r.Seq).Select(r => r.Data)
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(d => JsonSerializer.Deserialize<Dictionary<string, string>>(d)![key])];
    }
}
