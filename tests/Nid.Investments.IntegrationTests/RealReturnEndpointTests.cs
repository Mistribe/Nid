using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Investments.IntegrationTests;

/// <summary>Inflation PR 2 (C10): the real annual return of an envelope and of a holding.</summary>
public class RealReturnEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task PostAsync(NidApp app, string url, object body)
        => Assert.Equal(System.Net.HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, url, body)).StatusCode);

    private static async Task<InflationSeries> SeriesAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInflation>().ReadAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(ItemId Id, JsonElement Detail)> EnvelopeAsync(NidApp app, string currency = "EUR")
    {
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared, currency);
        await PostAsync(app, $"{Url(id)}/flows", new { occurredOn = "2022-10-31", kind = "opening", amount = new { amount = "20000.00", currency } });
        await PostAsync(app, $"{Url(id)}/valuations", new { asOf = "2025-09-30", amount = new { amount = "25000.00", currency } });
        return (id, await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(id))));
    }

    [Fact]
    public async Task Envelope_detail_gives_the_real_annual_return()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2022, 1), new YearMonth(2025, 9), 1.002m);
        var (_, detail) = await EnvelopeAsync(app);

        var expected = (await SeriesAsync(app)).RealAnnualReturn(
            [(new LocalDate(2022, 10, 31), -20000m), (new LocalDate(2025, 9, 30), 25000m)], new LocalDate(2025, 9, 30), Currency.Eur);
        var real = detail.GetProperty("realReturn");
        Assert.NotNull(expected.Percent);
        Assert.Equal(expected.Percent, real.GetProperty("percent").GetDecimal());
        Assert.Equal("2025-09", real.GetProperty("inflationTo").GetString());
        Assert.True(real.GetProperty("percent").GetDecimal() < detail.GetProperty("figures").GetProperty("annualReturnPct").GetDecimal());
    }

    [Fact]
    public async Task Envelope_detail_without_a_cpi_series_keeps_the_nominal_return_and_says_unavailable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, detail) = await EnvelopeAsync(app);

        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("figures").GetProperty("annualReturnPct").ValueKind);
        var real = detail.GetProperty("realReturn");
        Assert.Equal(JsonValueKind.Null, real.GetProperty("percent").ValueKind);
        Assert.Equal("unavailable", real.GetProperty("missing").GetString());
    }

    [Fact]
    public async Task Envelope_detail_in_usd_has_no_real_return()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2022, 1), new YearMonth(2025, 9), 1.002m);
        var (_, detail) = await EnvelopeAsync(app, "USD");

        var real = detail.GetProperty("realReturn");
        Assert.Equal(JsonValueKind.Null, real.GetProperty("percent").ValueKind);
        Assert.Equal("currency", real.GetProperty("missing").GetString());
    }

    [Fact]
    public async Task Envelope_detail_under_twelve_months_has_none()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2022, 1), new YearMonth(2025, 9), 1.002m);
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await PostAsync(app, $"{Url(id)}/flows", new { occurredOn = "2025-06-30", kind = "opening", amount = Eur("20000.00") });
        await PostAsync(app, $"{Url(id)}/valuations", new { asOf = "2025-09-30", amount = Eur("21000.00") });

        var real = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(id)))).GetProperty("realReturn");
        Assert.All(new[] { "percent", "inflationTo", "missing" }, p => Assert.Equal(JsonValueKind.Null, real.GetProperty(p).ValueKind));
    }

    [Fact]
    public async Task Holding_gives_its_real_annual_return()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2022, 1), new YearMonth(2025, 9), 1.002m);
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", new { name = "Fonds euros", kind = "euro_fund" });
        var holding = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await PostAsync(app, $"{Url(id)}/holdings/{holding}/movements", new { occurredOn = "2023-01-01", kind = "allocation_in", amount = Eur("7000.00") });
        await PostAsync(app, $"{Url(id)}/holding-snapshots", new { asOf = "2024-12-31", lines = new object[] { new { holdingId = holding, value = Eur("8000.00") } } });

        var read = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings"));
        var real = read.GetProperty("holdings")[0].GetProperty("realReturn");
        var expected = (await SeriesAsync(app)).RealAnnualReturn(
            [(new LocalDate(2023, 1, 1), -7000m), (new LocalDate(2024, 12, 31), 8000m)], new LocalDate(2024, 12, 31), Currency.Eur);
        Assert.NotNull(expected.Percent);
        Assert.Equal(expected.Percent, real.GetProperty("percent").GetDecimal());
        Assert.Equal("2024-12", real.GetProperty("inflationTo").GetString());
    }
}
