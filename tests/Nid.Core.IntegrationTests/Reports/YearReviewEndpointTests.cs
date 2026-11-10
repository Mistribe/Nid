using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Reports;

/// <summary>
/// Issue #12: <c>GET /reports/year</c> agrees with the Performance page and the net worth curve. Today is 2025-10-15: 2024 is the
/// full year, 2025 the year to date.
/// </summary>
public class YearReviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Review = "/api/v1/reports/year";
    private const string Performance = "/api/v1/investments/overview/performance";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string? A(JsonElement element, string name)
        => element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetProperty("amount").GetString();

    private static decimal D(string? amount) => decimal.Parse(amount!, CultureInfo.InvariantCulture);

    /// <summary>A PEA opened with 20 000 on 31/12/2023 worth 21 000, 2 000 paid in on 15/06/2024, worth 25 000 on 31/12/2024 and 26 000 on 30/09/2025.</summary>
    private static async Task<ItemId> PeaAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync(name, owner, visibility);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2023-12-31", kind = "opening", amount = Eur("20000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2024-06-15", kind = "contribution", amount = Eur("2000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2023-12-31", amount = Eur("21000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2024-12-31", amount = Eur("25000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("26000.00") });
        return id;
    }

    [Fact]
    public async Task The_investments_section_matches_the_performance_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var late = await app.NewEnvelopeAsync("Récent", app.AdultB, Visibility.Shared);
        await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/investments/envelopes/{late.Value}/valuations", new { asOf = "2024-03-01", amount = Eur("100.00") });

        var review = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"))).GetProperty("investments");
        var page = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?scope=household&from=2023-12-31&to=2024-12-31"));

        Assert.Equal(("21000.00", "25100.00", "2100.00"), (A(review, "startValue"), A(review, "endValue"), A(review, "gain")));
        Assert.Equal((A(page, "startValue"), A(page, "endValue"), A(page, "contributions"), A(page, "withdrawals"), A(page, "performance")),
            (A(review, "startValue"), A(review, "endValue"), A(review, "contributions"), A(review, "withdrawals"), A(review, "gain")));
        Assert.Equal(page.GetProperty("periodReturnPct").GetDecimal(), review.GetProperty("periodReturnPct").GetDecimal());
        static IEnumerable<(Guid, string?, string?)> Rows(JsonElement body)
            => body.GetProperty("envelopes").EnumerateArray().Select(r => (r.GetProperty("id").GetGuid(), A(r, "startValue"), A(r, "endValue")));
        Assert.Equal(Rows(page), Rows(review));
    }

    [Fact]
    public async Task The_net_worth_matches_the_curve()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));

        var worth = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"))).GetProperty("netWorth");
        var points = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth?scope=household&period=3y")))
            .GetProperty("points").EnumerateArray().ToDictionary(p => p.GetProperty("on").GetString()!, p => A(p, "value"));

        Assert.Equal((points["2023-12-31"], points["2024-12-31"]), (A(worth, "start"), A(worth, "end")));
        Assert.Equal(D(A(worth, "end")) - D(A(worth, "start")), D(A(worth, "change")));
        Assert.Equal(D(A(worth, "change")), worth.GetProperty("parts").EnumerateArray().Sum(p => D(A(p, "amount"))));
    }

    [Fact]
    public async Task An_envelope_closed_in_the_year_agrees()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var closed = await app.NewEnvelopeAsync("Clos", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{closed.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2023-12-31", kind = "opening", amount = Eur("10000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2023-12-31", amount = Eur("10500.00") });
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close",
            new { closedOn = "2024-10-01", finalWithdrawal = Eur("11000.00") })).StatusCode);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"));

        var investments = body.GetProperty("investments");
        var parts = body.GetProperty("netWorth").GetProperty("parts").EnumerateArray()
            .Where(p => p.GetProperty("key").GetString()!.StartsWith("investments.", StringComparison.Ordinal)).Sum(p => D(A(p, "amount")));
        Assert.Equal(D(A(investments, "endValue")) - D(A(investments, "startValue")), parts);
        Assert.Equal(2, investments.GetProperty("envelopes").GetArrayLength());
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task Another_adults_private_items_are_left_out(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var hidden = await PeaAsync(app, "Secret de B", app.AdultB, Visibility.Private);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope={scope}"));

        Assert.Equal([mine.Value], body.GetProperty("investments").GetProperty("envelopes").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal("25000.00", A(body.GetProperty("netWorth"), "end"));
        Assert.DoesNotContain(hidden.Value.ToString(), body.ToString());
        Assert.DoesNotContain("Secret de B", body.ToString());
    }

    [Fact]
    public async Task The_current_year_runs_to_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?scope=household"));
        // The previous period ends on the same day a year earlier.
        var previous = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?scope=household&from=2023-12-31&to=2024-10-15"));

        Assert.Equal((2025, "2024-12-31", "2025-10-15", true),
            (body.GetProperty("year").GetInt32(), body.GetProperty("start").GetString(), body.GetProperty("end").GetString(), body.GetProperty("toDate").GetBoolean()));
        Assert.Equal([2023, 2024, 2025], body.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal(A(previous, "performance"), A(body.GetProperty("investments"), "previousGain"));
        Assert.Equal(("25000.00", "26000.00"), (A(body.GetProperty("netWorth"), "start"), A(body.GetProperty("netWorth"), "end")));
    }

    [Fact]
    public async Task A_future_year_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);

        var future = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2026");
        var before = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2022");
        var unreadable = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=soon");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (future.StatusCode, before.StatusCode, unreadable.StatusCode));
        Assert.Contains("reports.invalid_year", await future.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("reports.invalid_year", await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
