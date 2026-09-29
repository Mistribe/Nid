using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Host;

[Collection("postgres")]
public class CapabilitiesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Stage_one_enables_core_subscriptions_and_import()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var capabilities = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/platform/capabilities");

        Assert.Equal(["core", "subscriptions", "import", "investments"],
            capabilities.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!).ToArray());
    }

    [Fact]
    public async Task Capabilities_say_whether_the_actor_is_admin()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var a = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/platform/capabilities");
        var b = await app.GetAsync<JsonElement>(app.AdultB, "/api/v1/platform/capabilities");

        Assert.True(a.GetProperty("admin").GetBoolean());
        Assert.False(b.GetProperty("admin").GetBoolean());
    }
}
