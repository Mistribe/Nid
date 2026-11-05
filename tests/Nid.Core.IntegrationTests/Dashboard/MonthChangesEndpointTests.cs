using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>Issue #415: GET /dashboard/changes compares today (2025-10-15) with a month ago (2025-09-15).</summary>
public class MonthChangesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task PostAsync(NidApp app, string url, object body)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, url, body)).StatusCode);

    private static Task ValueAsync(NidApp app, ItemId envelope, string asOf, string amount)
        => PostAsync(app, $"/api/v1/investments/envelopes/{envelope.Value}/valuations", new { asOf, amount = new { amount, currency = "EUR" }, note = (string?)null });

    private static async Task<JsonElement> ChangesAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/changes?scope=me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_first_month_has_nothing_to_compare()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        await ValueAsync(app, livret, "2025-10-01", "100.00");

        var changes = await ChangesAsync(app);

        Assert.True(changes.GetProperty("isEmpty").GetBoolean());
        Assert.Equal("2025-09-15", changes.GetProperty("since").GetString());
        Assert.Equal(JsonValueKind.Null, changes.GetProperty("netWorth").ValueKind);
    }

    [Fact]
    public async Task A_contribution_and_a_gain_are_the_net_worths_causes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, pea, "2025-09-01", "100.00");
        await PostAsync(app, $"/api/v1/investments/envelopes/{pea.Value}/flows",
            new { occurredOn = "2025-10-02", kind = "contribution", amount = new { amount = "50.00", currency = "EUR" } });
        await ValueAsync(app, pea, "2025-10-05", "170.00");

        var worth = (await ChangesAsync(app)).GetProperty("netWorth");

        Assert.Equal(("100.00", "170.00", "70.00"), (
            worth.GetProperty("before").GetProperty("amount").GetString(),
            worth.GetProperty("now").GetProperty("amount").GetString(),
            worth.GetProperty("change").GetProperty("amount").GetString()));
        Assert.Equal([("investments.paid_in", "50.00"), ("investments.value", "20.00")],
            worth.GetProperty("causes").EnumerateArray().Select(c => (c.GetProperty("key").GetString(), c.GetProperty("change").GetProperty("amount").GetString())));
    }
}
