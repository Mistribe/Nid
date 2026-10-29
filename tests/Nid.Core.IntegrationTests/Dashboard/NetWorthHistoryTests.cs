using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests.Dashboard;

public class NetWorthHistoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2025, 10, 15);

    private static async Task ValueAsync(NidApp app, PersonId writer, ItemId envelope, string asOf, string amount, string currency = "EUR")
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(writer, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf, amount = new { amount, currency }, note = (string?)null })).StatusCode);

    private static async Task ContributionAsync(NidApp app, ItemId envelope, string on, string amount)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/flows",
            new { occurredOn = on, kind = "contribution", amount = new { amount, currency = "EUR" } })).StatusCode);

    private static async Task<List<JsonElement>> PointsAsync(NidApp app, PersonId reader, string query)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, "/api/v1/dashboard/net-worth" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("points").EnumerateArray().ToList();
    }

    private static JsonElement At(List<JsonElement> points, string on) => points.Single(p => p.GetProperty("on").GetString() == on);
    private static string? Amount(JsonElement point, string field)
        => point.GetProperty(field).ValueKind == JsonValueKind.Null ? null : point.GetProperty(field).GetProperty("amount").GetString();

    private sealed class FixedRates(RateTable table) : IFxRates
    {
        public Task<RateTable> LoadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct) => Task.FromResult(table);
    }

    private sealed class FakeHistory(Func<IReadOnlyList<LocalDate>, Result<IReadOnlyList<NetWorthPoint>>> history) : INetWorthContribution
    {
        public Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
            => Task.FromResult(Result.Success(new NetWorthPart(0, 0, 0, null, new Dictionary<Currency, decimal>(), new Dictionary<Currency, decimal>(),
                new Dictionary<Currency, decimal>(), new Dictionary<Currency, decimal>(), [], 0)));
        public Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
            => Task.FromResult(history(dates));
        public Task<Result<IReadOnlyDictionary<Currency, decimal>>> NetContributionsAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
            => Task.FromResult(Result.Success<IReadOnlyDictionary<Currency, decimal>>(new Dictionary<Currency, decimal>()));
    }

    [Fact]
    public async Task A_failing_contribution_fails_the_request()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<INetWorthContribution>(_ =>
            new FakeHistory(_ => Result.Failure<IReadOnlyList<NetWorthPoint>>(new Failure("test.broken", "History unreadable.", null)))));

        // An unknown failure code maps to 422 (ResultHttp.ProblemFor).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth")).StatusCode);
    }

    [Fact]
    public async Task Points_of_two_contributions_are_merged()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<INetWorthContribution>(_ =>
            new FakeHistory(dates => Result.Success<IReadOnlyList<NetWorthPoint>>(dates.Select(d => new NetWorthPoint(
                d, new Dictionary<Currency, decimal> { [Currency.Eur] = 100m }, new Dictionary<Currency, decimal> { [Currency.Eur] = 80m }, 1)).ToList()))));
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, pea, "2025-02-28", "1000.00");

        var points = await PointsAsync(app, app.AdultA, "?period=12m");

        Assert.Equal("1100.00", Amount(At(points, "2025-09-30"), "value"));
        Assert.Equal("80.00", Amount(At(points, "2025-09-30"), "contributions"));
    }

    [Fact]
    public async Task An_envelope_closed_on_a_grid_date_stops_counting_that_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, pea, "2025-03-31", "500.00");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{pea.Value}/close", new { closedOn = "2025-05-31" }))
            .EnsureSuccessStatusCode();

        var points = await PointsAsync(app, app.AdultA, "?period=12m");

        Assert.Equal("500.00", Amount(At(points, "2025-04-30"), "value"));
        Assert.Null(Amount(At(points, "2025-05-31"), "value"));
    }

    [Fact]
    public async Task The_grid_is_each_month_end_then_today()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var twelve = await PointsAsync(app, app.AdultA, "");
        var three = await PointsAsync(app, app.AdultA, "?period=3y");

        Assert.Equal(13, twelve.Count);
        Assert.Equal("2024-10-31", twelve[0].GetProperty("on").GetString());
        Assert.Equal("2025-09-30", twelve[11].GetProperty("on").GetString());
        Assert.Equal("2025-10-15", twelve[12].GetProperty("on").GetString());
        Assert.Equal(37, three.Count);
        Assert.Equal("2022-10-31", three[0].GetProperty("on").GetString());
        Assert.All(twelve, p => Assert.Null(Amount(p, "value"))); // nothing recorded: no value, no missing rate
        Assert.All(twelve, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("missingCurrency").ValueKind));
    }

    [Fact]
    public async Task Readings_carry_forward_and_contributions_stop_at_the_reading()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ContributionAsync(app, pea, "2025-02-10", "1000.00");
        await ValueAsync(app, app.AdultA, pea, "2025-02-28", "1000.00");
        await ContributionAsync(app, pea, "2025-05-15", "500.00");
        await ValueAsync(app, app.AdultA, pea, "2025-06-30", "1600.00");
        await ContributionAsync(app, pea, "2025-09-20", "200.00"); // after the last reading

        var points = await PointsAsync(app, app.AdultA, "?period=12m");

        Assert.Null(Amount(At(points, "2025-01-31"), "value"));
        Assert.Equal("1000.00", Amount(At(points, "2025-02-28"), "value"));
        Assert.Equal("1000.00", Amount(At(points, "2025-05-31"), "value")); // carried forward
        Assert.Equal("1000.00", Amount(At(points, "2025-05-31"), "contributions")); // 05-15 comes after the 02-28 reading
        Assert.Equal("1600.00", Amount(At(points, "2025-06-30"), "value"));
        Assert.Equal("1500.00", Amount(At(points, "2025-06-30"), "contributions"));
        Assert.Equal("1600.00", Amount(At(points, "2025-10-15"), "value"));
        Assert.Equal("1500.00", Amount(At(points, "2025-10-15"), "contributions")); // 09-20 is after the reading
    }

    [Fact]
    public async Task All_starts_at_the_first_month_with_a_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, pea, "2025-02-28", "1000.00");

        var points = await PointsAsync(app, app.AdultA, "?period=all");

        Assert.Equal(9, points.Count); // 2025-02-28 … 2025-09-30, then today
        Assert.Equal("2025-02-28", points[0].GetProperty("on").GetString());
        Assert.Equal("2025-10-15", points[^1].GetProperty("on").GetString());
    }

    [Fact]
    public async Task All_with_no_reading_has_no_point()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Empty(await PointsAsync(app, app.AdultA, "?period=all"));
    }

    [Fact]
    public async Task A_closed_envelope_counts_until_its_closing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var old = await app.NewEnvelopeAsync("Ancien", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, old, "2025-03-31", "500.00");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{old.Value}/close", new { closedOn = "2025-06-01" }))
            .EnsureSuccessStatusCode();

        var points = await PointsAsync(app, app.AdultA, "?period=12m");

        Assert.Equal("500.00", Amount(At(points, "2025-05-31"), "value"));
        Assert.Null(Amount(At(points, "2025-06-30"), "value"));
        Assert.Equal(JsonValueKind.Null, At(points, "2025-06-30").GetProperty("missingCurrency").ValueKind);
    }

    [Fact]
    public async Task A_month_without_a_rate_is_a_gap_naming_the_currency()
    {
        var rates = new RateTable(
            [new FxRate(new LocalDate(2025, 9, 30), Currency.Eur, 0.9m, FxRateSource.Ecb), new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)],
            Today, maxGapDays: 7);
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IFxRates>(_ => new FixedRates(rates)));
        var dollars = await app.NewEnvelopeAsync("Dollars", app.AdultA, Visibility.Shared, "USD");
        await ValueAsync(app, app.AdultA, dollars, "2025-07-31", "100.00", "USD");

        var points = await PointsAsync(app, app.AdultA, "?period=12m");

        var august = At(points, "2025-08-31");
        Assert.Null(Amount(august, "value"));
        Assert.Null(Amount(august, "contributions"));
        Assert.Equal("USD", august.GetProperty("missingCurrency").GetString());
        Assert.Equal("90.00", Amount(At(points, "2025-09-30"), "value"));
        Assert.Equal("90.00", Amount(At(points, "2025-10-15"), "value"));
    }

    [Theory]
    [InlineData("?period=5y")]
    [InlineData("?scope=everyone")]
    public async Task An_unknown_period_or_scope_is_a_bad_request(string query)
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth" + query)).StatusCode);
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task The_other_adults_private_envelopes_open_or_closed_change_no_point(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var open = await app.NewEnvelopeAsync("Secret", app.AdultB, Visibility.Private);
        await ValueAsync(app, app.AdultB, open, "2025-03-31", "5000.00");
        var closed = await app.NewEnvelopeAsync("Secret clos", app.AdultB, Visibility.Private);
        await ValueAsync(app, app.AdultB, closed, "2025-02-28", "700.00");
        (await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/investments/envelopes/{closed.Value}/close", new { closedOn = "2025-06-01" }))
            .EnsureSuccessStatusCode();

        var points = await PointsAsync(app, app.AdultA, $"?scope={scope}&period=12m");

        Assert.All(points, p => Assert.Null(Amount(p, "value")));
    }
}
