using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

[Collection("postgres")]
public class EnvelopeEndpointTests(PostgresFixture fixture)
{
    private static object Create(NidApp app, string name = "PEA", object? opening = null, object? firstValuation = null, string visibility = "shared")
        => new
        {
            displayName = name, kind = "pea", currency = "EUR", visibility,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            institution = "Banque", accountHint = "4521", valuationCadence = "monthly",
            opening, firstValuation,
        };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Creating_with_an_opening_and_a_first_value_shows_the_gain()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", Create(app,
            opening: new { occurredOn = "2025-01-31", amount = new { amount = "20000.00", currency = "EUR" } },
            firstValuation: new { asOf = "2025-01-31", amount = new { amount = "21000.00", currency = "EUR" } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id}"));
        Assert.Equal("PEA", detail.GetProperty("displayName").GetString());
        Assert.Equal("pea", detail.GetProperty("kind").GetString());
        var figures = detail.GetProperty("figures");
        Assert.Equal("21000.0000", figures.GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("1000.0000", figures.GetProperty("gain").GetProperty("amount").GetString());
        Assert.Equal(5.0m, figures.GetProperty("gainPct").GetDecimal());
        Assert.Single(detail.GetProperty("valuations").EnumerateArray());
        Assert.Single(detail.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task A_refused_first_value_leaves_no_envelope_behind()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", Create(app,
            firstValuation: new { asOf = "2025-10-16", amount = new { amount = "1.00", currency = "EUR" } }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("core.date_in_future", (await JsonAsync(refused)).GetProperty("code").GetString());
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Items.CountAsync(i => i.Module == "investments"));
        await using var investments = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        Assert.Equal(0, await investments.Envelopes.CountAsync());
    }

    [Theory]
    [InlineData("kind", "casino", "investments.unknown_kind")]
    [InlineData("valuationCadence", "hourly", "investments.unknown_cadence")]
    [InlineData("accountHint", "12345", "investments.invalid_envelope")]
    public async Task A_bad_attribute_is_refused(string field, string value, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = JsonSerializer.SerializeToElement(Create(app));
        var patched = JsonSerializer.Deserialize<Dictionary<string, object?>>(body.GetRawText())!;
        patched[field] = value;

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", patched);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(code, (await JsonAsync(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Editing_changes_the_name_and_the_settings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var edited = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{id.Value}",
            new { displayName = "Livret A", valuationCadence = "quarterly", clearAccountHint = true });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}"));
        Assert.Equal(("Livret A", "quarterly"), (detail.GetProperty("displayName").GetString(), detail.GetProperty("valuationCadence").GetString()));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("accountHint").ValueKind);
    }

    [Fact]
    public async Task Another_adult_neither_sees_nor_reads_a_private_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);

        var list = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me"));
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"/api/v1/investments/envelopes/{id.Value}", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_subscription_is_not_an_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{subscription.Value}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{subscription.Value}", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_list_shows_shared_envelopes_in_the_household_scope_with_their_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewEnvelopeAsync("Partagé", app.AdultA, Visibility.Shared);
        await app.NewEnvelopeAsync("À moi", app.AdultA, Visibility.Private);

        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes"));
        var me = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me"));

        Assert.Equal(["Partagé"], household.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("displayName").GetString()));
        Assert.Equal(2, me.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("items")[0].GetProperty("figures").GetProperty("value").ValueKind);
    }
}
