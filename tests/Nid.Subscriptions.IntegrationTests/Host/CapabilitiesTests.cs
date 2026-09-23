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

        Assert.Equal(["core", "subscriptions", "import"],
            capabilities.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!).ToArray());
    }
}
