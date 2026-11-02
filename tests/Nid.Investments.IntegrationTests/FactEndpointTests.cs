using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Investments.IntegrationTests;

public class FactEndpointTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DetailAsync(NidApp app, ItemId id)
        => await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}"));

    [Fact]
    public async Task Values_and_flows_make_the_golden_case()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";

        foreach (var (on, kind, amount) in new[] { ("2024-12-31", "opening", "20000.00"), ("2025-06-15", "contribution", "2000.00") })
            Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows",
                new { occurredOn = on, kind, amount = Eur(amount) })).StatusCode);
        foreach (var (on, amount) in new[] { ("2024-12-31", "21000.00"), ("2025-09-30", "25000.00") })
            Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations",
                new { asOf = on, amount = Eur(amount) })).StatusCode);

        var figures = (await DetailAsync(app, id)).GetProperty("figures");
        Assert.Equal("3000.0000", figures.GetProperty("gain").GetProperty("amount").GetString());
        Assert.Equal(13.6m, figures.GetProperty("gainPct").GetDecimal());
    }

    [Fact]
    public async Task Correcting_and_voiding_a_flow_changes_the_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("1000.00") });
        var flow = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows",
            new { occurredOn = "2025-01-10", kind = "contribution", amount = Eur("90.00") }))).GetProperty("id").GetGuid();

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/flows/{flow}",
            new { occurredOn = "2025-01-10", kind = "contribution", amount = Eur("900.00") });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var replacement = (await JsonAsync(corrected)).GetProperty("id").GetGuid();
        Assert.Equal("100.0000", (await DetailAsync(app, id)).GetProperty("figures").GetProperty("gain").GetProperty("amount").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}/flows/{flow}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}/flows/{replacement}")).StatusCode);
        Assert.Equal("1000.0000", (await DetailAsync(app, id)).GetProperty("figures").GetProperty("gain").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Voiding_a_value_brings_back_the_previous_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-08-31", amount = Eur("800.00") });
        var september = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations",
            new { asOf = "2025-09-30", amount = Eur("900.00") }))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}/valuations/{september}")).StatusCode);

        Assert.Equal("800.0000", (await DetailAsync(app, id)).GetProperty("figures").GetProperty("value").GetProperty("amount").GetString());
    }

    [Theory]
    [InlineData("valuations", "{\"asOf\":\"2025-10-16\",\"amount\":{\"amount\":\"1.00\",\"currency\":\"EUR\"}}", 422, "core.date_in_future")]
    [InlineData("valuations", "{\"asOf\":\"2025-09-30\",\"amount\":{\"amount\":\"1.00\",\"currency\":\"USD\"}}", 422, "core.currency_mismatch")]
    [InlineData("flows", "{\"occurredOn\":\"2025-09-30\",\"kind\":\"gift\",\"amount\":{\"amount\":\"1.00\",\"currency\":\"EUR\"}}", 422, "core.invalid_fact")]
    [InlineData("flows", "{\"occurredOn\":\"2025-09-30\",\"kind\":\"contribution\",\"amount\":{\"amount\":\"0.00\",\"currency\":\"EUR\"}}", 422, "core.amount_not_positive")]
    public async Task A_refused_fact_answers_with_the_core_code(string path, string body, int status, string code)
    {
        var app = shared.App;
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/{path}", body);

        Assert.Equal(status, (int)refused.StatusCode);
        Assert.Equal(code, (await JsonAsync(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_second_opening_is_a_conflict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}/flows";
        await app.RawAsync(app.AdultA, HttpMethod.Post, url, new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("1.00") });

        var second = await app.RawAsync(app.AdultA, HttpMethod.Post, url, new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("2.00") });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("core.opening_exists", (await JsonAsync(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Facts_on_a_subscription_are_not_found_and_another_adult_cannot_write_a_shared_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);
        var shared = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var value = new { asOf = "2025-09-30", amount = Eur("1.00") };

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{subscription.Value}/valuations", value)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/investments/envelopes/{shared.Value}/valuations", value)).StatusCode);
    }

    // Controller ruling carried from Task 4: System.Text.Json binds a missing or null body member to a real
    // null even for a non-nullable type, so a malformed body must never reach a NullReferenceException — every
    // new route answers 400 (thrown BadHttpRequestException, caught by MalformedInputFilter) and writes nothing.

    [Fact]
    public async Task A_valuation_with_no_date_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/valuations",
            "{\"amount\":{\"amount\":\"1.00\",\"currency\":\"EUR\"}}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty((await DetailAsync(app, id)).GetProperty("valuations").EnumerateArray());
    }

    [Fact]
    public async Task A_valuation_with_no_amount_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/valuations",
            "{\"asOf\":\"2025-09-30\"}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty((await DetailAsync(app, id)).GetProperty("valuations").EnumerateArray());
    }

    [Fact]
    public async Task A_flow_with_no_date_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/flows",
            "{\"kind\":\"contribution\",\"amount\":{\"amount\":\"1.00\",\"currency\":\"EUR\"}}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty((await DetailAsync(app, id)).GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task A_flow_with_no_amount_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/flows",
            "{\"occurredOn\":\"2025-09-30\",\"kind\":\"contribution\"}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty((await DetailAsync(app, id)).GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task Correcting_a_flow_with_no_amount_is_a_bad_request_and_leaves_the_flow_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        var flow = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows",
            new { occurredOn = "2025-01-10", kind = "contribution", amount = Eur("90.00") }))).GetProperty("id").GetGuid();

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Put, $"{url}/flows/{flow}",
            "{\"occurredOn\":\"2025-01-10\",\"kind\":\"contribution\"}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var flows = (await DetailAsync(app, id)).GetProperty("flows").EnumerateArray().ToList();
        Assert.Single(flows);
        Assert.Equal("90.0000", flows[0].GetProperty("amount").GetProperty("amount").GetString());
    }
    private static async Task<ItemId> NewEnvelopeOfKindAsync(NidApp app, string kind)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = kind, kind, currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        response.EnsureSuccessStatusCode();
        return new ItemId((await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    private static async Task<string> GainAsync(NidApp app, ItemId id)
        => (await DetailAsync(app, id)).GetProperty("figures").GetProperty("gain").GetProperty("amount").GetString()!;

    private static async Task<HttpResponseMessage> PostFlowAsync(NidApp app, ItemId id, string kind, string? category)
        => await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/flows",
            new { occurredOn = "2025-06-15", kind, amount = Eur("100.00"), category });

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal("category", body.GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_withdrawal_can_be_recorded_as_a_dividend()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var plain = await NewEnvelopeOfKindAsync(app, "pea");
        var classified = await NewEnvelopeOfKindAsync(app, "pea");
        foreach (var id in new[] { plain, classified })
        {
            await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/flows", new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("1000.00") });
            await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/valuations", new { asOf = "2025-09-30", amount = Eur("1200.00") });
        }
        Assert.Equal(HttpStatusCode.Created, (await PostFlowAsync(app, plain, "withdrawal", null)).StatusCode);

        var recorded = await PostFlowAsync(app, classified, "withdrawal", "dividend");

        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal("dividend", (await core.Flows.SingleAsync(f => f.ItemId == classified.Value && f.Kind == "withdrawal")).Category);
        Assert.Equal(await GainAsync(app, plain), await GainAsync(app, classified));
    }

    [Theory]
    [InlineData("pea", "contribution")]
    [InlineData("savings", "withdrawal")]
    public async Task An_income_category_off_a_withdrawal_or_on_a_savings_account_is_refused(string envelopeKind, string flowKind)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewEnvelopeOfKindAsync(app, envelopeKind);

        await AssertRefusedAsync(await PostFlowAsync(app, id, flowKind, "dividend"), "investments.invalid_income_category");
    }

    [Fact]
    public async Task A_withdrawal_is_reclassified_by_a_correction()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewEnvelopeOfKindAsync(app, "pea");
        var url = $"/api/v1/investments/envelopes/{id.Value}/flows";
        var flow = (await JsonAsync(await PostFlowAsync(app, id, "withdrawal", null))).GetProperty("id").GetGuid();

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{flow}",
            new { occurredOn = "2025-06-15", kind = "withdrawal", amount = Eur("100.00"), category = "interest" });

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var replacement = (await JsonAsync(corrected)).GetProperty("id").GetGuid();
        Assert.Equal("interest", (await core.Flows.SingleAsync(f => f.Id == replacement)).Category);
    }

    [Fact]
    public async Task A_transfer_leg_cannot_be_reclassified()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var from = await NewEnvelopeOfKindAsync(app, "pea");
        var to = await NewEnvelopeOfKindAsync(app, "pea");
        var transfer = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = from.Value, to = to.Value, occurredOn = "2025-06-15", amount = Eur("100.00") }))).GetProperty("id").GetGuid();
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var leg = await core.Flows.SingleAsync(f => f.ExternalRef == $"transfer:{transfer}" && f.Kind == "withdrawal");
        var url = $"/api/v1/investments/envelopes/{from.Value}/flows/{leg.Id}";

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, url,
            new { occurredOn = "2025-06-15", kind = "withdrawal", amount = Eur("100.00"), category = "dividend" }), "investments.transfer_leg");

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, url,
            new { occurredOn = "2025-06-15", kind = "withdrawal", amount = Eur("100.00"), category = "transfer" })).StatusCode);
    }
}
