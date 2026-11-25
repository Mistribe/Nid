using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

public class CapabilitiesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Stage_one_enables_core_subscriptions_and_import()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var capabilities = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/platform/capabilities");

        Assert.Equal(["core", "subscriptions", "import", "investments", "income", "realestate", "goals", "documents", "banking", "childcare", "assets"],
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

    [Fact]
    public async Task Version_is_the_api_build_version()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var version = await app.GetAsync<JsonElement>(app.AdultB, "/api/v1/platform/version");

        Assert.Matches(@"^\d+\.\d+\.\d+", version.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Every_response_carries_the_api_version_even_a_refusal()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var version = (await app.GetAsync<JsonElement>(app.AdultB, "/api/v1/platform/version")).GetProperty("version").GetString();
        using var ok = await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/platform/capabilities");
        using var refused = await app.RawAnonymousAsync(HttpMethod.Get, "/api/v1/platform/capabilities");

        Assert.Equal(version, Assert.Single(ok.Headers.GetValues("X-Nid-Version")));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(version, Assert.Single(refused.Headers.GetValues("X-Nid-Version")));
    }
}
