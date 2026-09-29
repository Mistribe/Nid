using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

[Collection("postgres")]
public class TransferAndCloseTests(PostgresFixture fixture)
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<int> FlowCountAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        return await core.Flows.CountAsync(f => f.RetiredAt == null);
    }

    [Fact]
    public async Task A_transfer_writes_both_legs_with_one_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var transfer = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = livret.Value, to = pea.Value, occurredOn = "2025-06-15", amount = Eur("2000.00") });

        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        var id = (await JsonAsync(transfer)).GetProperty("id").GetGuid();
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var legs = await core.Flows.Where(f => f.ExternalRef == $"transfer:{id}").OrderBy(f => f.Kind).ToListAsync();
        Assert.Equal(
            [("contribution", pea.Value, "transfer"), ("withdrawal", livret.Value, "transfer")],
            legs.Select(f => (f.Kind, f.ItemId, f.Category!)));
        Assert.All(legs, f => Assert.Equal(2000m, f.Amount));
    }

    [Theory]
    [InlineData(true, false, "investments.transfer_same_envelope")]
    [InlineData(false, true, "investments.transfer_currency_mismatch")]
    public async Task A_transfer_to_itself_or_across_currencies_is_refused(bool same, bool usd, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var from = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var to = same ? from : await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, usd ? "USD" : "EUR");

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = from.Value, to = to.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(code, (await JsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(0, await FlowCountAsync(app));
    }

    [Fact]
    public async Task A_transfer_to_an_envelope_the_actor_cannot_write_is_refused_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewEnvelopeAsync("Mien", app.AdultA, Visibility.Shared);
        var theirs = await app.NewEnvelopeAsync("Sien", app.AdultB, Visibility.Shared);
        var hidden = await app.NewEnvelopeAsync("Caché", app.AdultB, Visibility.Private);

        var toShared = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = mine.Value, to = theirs.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });
        var toHidden = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = mine.Value, to = hidden.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });

        // Controller ruling, fix round 1: the destination is never disclosed — forbidden (readable, not
        // writable) and hidden (not readable) answer the same "not found", exactly as a purchase's grant
        // target (ItemWriteGuard.EnsureWritableAsync).
        Assert.Equal(HttpStatusCode.NotFound, toShared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, toHidden.StatusCode);
        Assert.Equal(0, await FlowCountAsync(app));
    }

    [Fact]
    public async Task Closing_then_reopening_an_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("500.00") });

        var closed = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-09-30", finalWithdrawal = Eur("510.00") });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url));
        Assert.True(detail.GetProperty("closed").GetBoolean());
        Assert.Equal("0.0000", detail.GetProperty("figures").GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("10.0000", detail.GetProperty("figures").GetProperty("gain").GetProperty("amount").GetString());
        Assert.Empty((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes"))).GetProperty("items").EnumerateArray());
        Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes?archived=true"))).GetProperty("items").EnumerateArray());

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-10-01", amount = Eur("1.00") });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("investments.envelope_closed", (await JsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-10-01" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/reopen")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-10-01", amount = Eur("1.00") })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/reopen")).StatusCode);
    }

    [Fact]
    public async Task The_period_performance_and_a_reversed_period()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2024-12-31", amount = Eur("21000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-06-15", kind = "contribution", amount = Eur("2000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("25000.00") });

        var period = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/performance?from=2024-12-31&to=2025-09-30"));
        Assert.Equal("2000.0000", period.GetProperty("performance").GetProperty("amount").GetString());

        var reversed = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/performance?from=2025-09-30&to=2024-12-31");
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
    }

    // Controller ruling carried from Task 4: System.Text.Json binds a missing or null body member to a real
    // null even for a non-nullable type, so a malformed body must never reach a NullReferenceException — every
    // new route answers 400 (thrown BadHttpRequestException, caught by MalformedInputFilter) and writes nothing.
    // A missing from/to Guid binds to Guid.Empty, which the pipeline treats as a missing item: 404, not a 500.

    [Fact]
    public async Task A_transfer_with_no_occurred_on_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var from = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var to = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            $"{{\"from\":\"{from.Value}\",\"to\":\"{to.Value}\",\"amount\":{{\"amount\":\"10.00\",\"currency\":\"EUR\"}}}}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, await FlowCountAsync(app));
    }

    [Fact]
    public async Task A_transfer_with_no_amount_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var from = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var to = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            $"{{\"from\":\"{from.Value}\",\"to\":\"{to.Value}\",\"occurredOn\":\"2025-06-15\"}}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, await FlowCountAsync(app));
    }

    [Fact]
    public async Task A_transfer_with_a_missing_from_or_to_is_not_found_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var to = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { to = to.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(0, await FlowCountAsync(app));
    }

    [Fact]
    public async Task Closing_with_no_closed_on_is_a_bad_request_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/close", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}"));
        Assert.False(detail.GetProperty("closed").GetBoolean());
    }
}
