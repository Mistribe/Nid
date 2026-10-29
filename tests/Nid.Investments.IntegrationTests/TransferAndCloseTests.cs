using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Investments.IntegrationTests;

public class TransferAndCloseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<int> FlowCountAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        return await core.Flows.CountAsync(f => f.RetiredAt == null);
    }

    private static async Task<Envelope> StoredAsync(NidApp app, ItemId id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IEnvelopeStore>().FindAsync(id, default))!;
    }

    [Fact]
    public async Task Closing_records_the_closing_date_and_reopening_clears_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-06-30", amount = Eur("100.00") });

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-07-31" })).StatusCode);
        Assert.Equal(new LocalDate(2025, 7, 31), (await StoredAsync(app, id)).ClosedOn);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/reopen")).StatusCode);
        Assert.Null((await StoredAsync(app, id)).ClosedOn);
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

    /// <summary>
    /// Minor 1: the mismatch is answered from comparing the two envelopes' currencies directly, whichever
    /// currency the amount itself carries — not only when the amount happens to be in the source's own
    /// currency (the only shape the second leg's write used to catch).
    /// </summary>
    [Fact]
    public async Task A_transfer_whose_amount_is_in_the_destinations_currency_is_still_a_currency_mismatch()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var from = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var to = await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, "USD");

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = from.Value, to = to.Value, occurredOn = "2025-06-15", amount = new { amount = "10.00", currency = "USD" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("investments.transfer_currency_mismatch", (await JsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(0, await FlowCountAsync(app));
    }

    /// <summary>access-tests.md §4 / 2B1a spec §8: a transfer only moves value between the two envelopes.</summary>
    [Fact]
    public async Task A_transfer_leaves_the_household_total_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var livretUrl = $"/api/v1/investments/envelopes/{livret.Value}";
        var peaUrl = $"/api/v1/investments/envelopes/{pea.Value}";

        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{livretUrl}/flows", new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("1000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{livretUrl}/valuations", new { asOf = "2025-01-01", amount = Eur("1000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{peaUrl}/flows", new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("500.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{peaUrl}/valuations", new { asOf = "2025-01-01", amount = Eur("500.00") });

        static async Task<decimal> NetContributionsAsync(NidApp app, string url)
            => decimal.Parse((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url)))
                .GetProperty("figures").GetProperty("netContributions").GetProperty("amount").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);

        var before = await NetContributionsAsync(app, livretUrl) + await NetContributionsAsync(app, peaUrl);

        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = livret.Value, to = pea.Value, occurredOn = "2025-06-15", amount = Eur("200.00") })).StatusCode);
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{livretUrl}/valuations", new { asOf = "2025-07-01", amount = Eur("800.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{peaUrl}/valuations", new { asOf = "2025-07-01", amount = Eur("700.00") });

        var after = await NetContributionsAsync(app, livretUrl) + await NetContributionsAsync(app, peaUrl);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task A_transfer_from_or_to_a_closed_envelope_is_refused_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var open = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var closed = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{closed.Value}/close", new { closedOn = "2025-06-01" })).StatusCode);

        var fromClosed = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = closed.Value, to = open.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });
        var toClosed = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = open.Value, to = closed.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });

        Assert.Equal(HttpStatusCode.Conflict, fromClosed.StatusCode);
        Assert.Equal("investments.envelope_closed", (await JsonAsync(fromClosed)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, toClosed.StatusCode);
        Assert.Equal("investments.envelope_closed", (await JsonAsync(toClosed)).GetProperty("code").GetString());
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

        // The destination is never disclosed — forbidden (readable, not writable) and hidden (not readable)
        // answer the same "not found", exactly as a purchase's grant target (ItemWriteGuard.EnsureWritableAsync).
        Assert.Equal(HttpStatusCode.NotFound, toShared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, toHidden.StatusCode);
        Assert.Equal(0, await FlowCountAsync(app));
    }

    [Fact]
    public async Task Closing_with_a_final_withdrawal_in_the_wrong_currency_is_refused_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("500.00") });

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close",
            new { closedOn = "2025-09-30", finalWithdrawal = new { amount = "10.00", currency = "USD" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("core.currency_mismatch", (await JsonAsync(refused)).GetProperty("code").GetString());

        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url));
        Assert.False(detail.GetProperty("closed").GetBoolean());
        Assert.Empty(detail.GetProperty("valuations").EnumerateArray());
        Assert.Equal(1, await FlowCountAsync(app)); // only the opening flow: no withdrawal was written
    }

    /// <summary>
    /// Important 1: a back-dated close must not leave a closed envelope showing a later valuation's value
    /// (or a later flow's contribution) as if nothing had happened. Same day as the latest fact is allowed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_close_dated_before_the_envelopes_latest_fact_is_refused_and_the_envelope_stays_open(bool laterIsAValuation)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-01-01", amount = Eur("10000.00") });
        if (laterIsAValuation)
            await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-06-01", amount = Eur("12000.00") });
        else
            await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-06-01", kind = "contribution", amount = Eur("500.00") });

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-03-01" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await JsonAsync(refused);
        Assert.Equal("investments.invalid_envelope", problem.GetProperty("code").GetString());
        Assert.Equal("closedOn", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url));
        Assert.False(detail.GetProperty("closed").GetBoolean());
    }

    /// <summary>Important 1: closing on the very day of the latest value is still allowed.</summary>
    [Fact]
    public async Task A_close_dated_the_same_day_as_the_latest_value_is_accepted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-01-01", amount = Eur("10000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-06-01", amount = Eur("12000.00") });

        var closed = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-06-01" });

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url));
        Assert.True(detail.GetProperty("closed").GetBoolean());
        Assert.Equal("0.0000", detail.GetProperty("figures").GetProperty("value").GetProperty("amount").GetString());
    }

    /// <summary>Minor 2: a closed envelope never reports staleness, however far past the cadence's threshold.</summary>
    [Fact]
    public async Task A_closed_envelope_reports_no_staleness_however_stale_it_would_otherwise_be()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-01-01", kind = "opening", amount = Eur("500.00") });

        // NidApp's clock is fixed at 2025-10-15: monthly cadence (the default) is stale past 45 days, and this
        // envelope closes 75 days before that — a live envelope would show a stale value.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-08-01" })).StatusCode);

        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url));
        Assert.True(detail.GetProperty("closed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("figures").GetProperty("staleDays").ValueKind);
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
        var reopenedAgain = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/reopen");
        Assert.Equal(HttpStatusCode.Conflict, reopenedAgain.StatusCode);
        Assert.Equal("investments.envelope_open", (await JsonAsync(reopenedAgain)).GetProperty("code").GetString());
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
        Assert.Equal("investments.invalid_period", (await JsonAsync(reversed)).GetProperty("code").GetString());
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
        var from = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var to = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var missingFrom = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { to = to.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });
        var missingTo = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = from.Value, occurredOn = "2025-06-15", amount = Eur("10.00") });

        Assert.Equal(HttpStatusCode.NotFound, missingFrom.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTo.StatusCode);
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
