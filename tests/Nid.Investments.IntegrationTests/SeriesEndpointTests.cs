using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Investments.IntegrationTests;

public class SeriesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task The_golden_case_gives_two_points_and_a_voided_value_drops_its_point()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2024-12-31", kind = "opening", amount = Eur("20000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-06-15", kind = "contribution", amount = Eur("2000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2024-12-31", amount = Eur("21000.00") });
        var last = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("25000.00") })))
            .GetProperty("id").GetGuid();

        var points = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/series"))).GetProperty("points");
        Assert.Equal(
            [("2024-12-31", "21000.0000", "20000.0000"), ("2025-09-30", "25000.0000", "22000.0000")],
            points.EnumerateArray().Select(p => (p.GetProperty("asOf").GetString(), p.GetProperty("value").GetProperty("amount").GetString(),
                p.GetProperty("netContributions").GetProperty("amount").GetString())));

        await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}/valuations/{last}");
        Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/series"))).GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task Another_adults_private_envelope_and_a_subscription_are_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);
        var subscription = await app.NewSubscriptionAsync("Spotify", app.AdultB, Visibility.Shared);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/investments/envelopes/{hidden.Value}/series")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/investments/envelopes/{subscription.Value}/series")).StatusCode);
    }

    [Fact]
    public async Task The_list_says_who_can_write_each_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewEnvelopeAsync("Partagé", app.AdultA, Visibility.Shared);

        var asOwner = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes"))).GetProperty("items")[0];
        var asOther = (await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/investments/envelopes"))).GetProperty("items")[0];

        Assert.True(asOwner.GetProperty("canWrite").GetBoolean());
        Assert.False(asOther.GetProperty("canWrite").GetBoolean());
    }
}
