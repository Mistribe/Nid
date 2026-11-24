using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.Tax.IntegrationTests.SaleContributionTests;
using static Nid.TestSupport.LoanApi;

namespace Nid.Tax.IntegrationTests;

/// <summary>#234: the taxable savings interest and the brokerage sales' gains of the year, through the real API.</summary>
public class CapitalTaxLinesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<IReadOnlyList<TaxLine>> LinesAsync(NidApp app, int year)
    {
        await using var services = app.Services.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var lines = new List<TaxLine>();
        foreach (var contribution in services.ServiceProvider.GetServices<ITaxContribution>())
        {
            Assert.True((await contribution.TaxLinesAsync(app.AdultA, VisibilityScope.Me, year, new LocalDate(2026, 3, 1), default)).TryGetValue(out var got));
            lines.AddRange(got!);
        }
        return lines.Where(l => l.Code is TaxLineCodes.SavingsInterest or TaxLineCodes.SavingsInterestUnknown
            or TaxLineCodes.RealisedGain or TaxLineCodes.RealisedGainUnknown).ToList();
    }

    [Fact]
    public async Task A_taxable_savings_account_gives_its_years_interest_and_a_regulated_livret_none()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var term = await EnvelopeAsync(app, "Compte à terme", "savings", "2024-06-01", opening: new { occurredOn = "2024-06-01", amount = Eur("10000") });
        await ValueAsync(app, term, "2024-12-31", "10100");
        await FlowAsync(app, term, "2025-03-01", "contribution", "1000");
        await ValueAsync(app, term, "2025-10-01", "11400");
        var livret = await EnvelopeAsync(app, "Livret A", "savings", "2024-06-01", opening: new { occurredOn = "2024-06-01", amount = Eur("5000") });
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Envelopes}/{livret.Value}", new { regulated = "livret_a" })).EnsureSuccessStatusCode();
        await ValueAsync(app, livret, "2025-10-01", "5100");
        await EnvelopeAsync(app, "Livret sans versement", "savings", "2024-06-01");

        var lines = await LinesAsync(app, 2025);

        // 11 400 − 10 100 − the 1 000 paid in.
        Assert.Equal(300m, Assert.Single(lines, l => l.Code == TaxLineCodes.SavingsInterest).Amount);
        Assert.Equal("Livret sans versement", Assert.Single(lines, l => l.Code == TaxLineCodes.SavingsInterestUnknown).Name);
        Assert.DoesNotContain(lines, l => l.Item == livret);

        var estimate = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/tax?year=2025&scope=me")).RootElement;
        Assert.Equal("300.00", estimate.GetProperty("inputs").GetProperty("capitalIncome").GetString());
        Assert.Contains(estimate.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "savings_interest_unknown");
    }

    [Fact]
    public async Task A_brokerage_sale_gains_its_amount_over_the_cost_per_unit_and_the_earlier_losses_offset_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var account = await EnvelopeAsync(app, "CTO", "brokerage", "2020-01-01");
        var url = $"{Envelopes}/{account.Value}";
        var etf = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "ETF", kind = "etf" }));
        var bond = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "Obligation", kind = "bond" }));
        await PostAsync(app, $"{url}/holding-snapshots", new
        {
            asOf = "2025-01-31",
            lines = new object[] { new { holdingId = etf, units = "10", unitPrice = "120", value = Eur("1200"), costBasisPerUnit = "100" } },
        });
        await PostAsync(app, $"{url}/holdings/{etf}/movements", new { occurredOn = "2025-06-01", kind = "allocation_out", amount = Eur("600"), units = "-4", unitPrice = "150" });
        await PostAsync(app, $"{url}/holdings/{bond}/movements", new { occurredOn = "2025-07-01", kind = "maturity", amount = Eur("1000") });
        await PostAsync(app, $"{url}/holdings/{etf}/movements", new { occurredOn = "2024-06-01", kind = "allocation_out", amount = Eur("500"), units = "-1", unitPrice = "500" });

        var lines = await LinesAsync(app, 2025);

        // 600 − 4 × 100; the bond's maturity has no units, so its gain is left out; the 2024 sale is another year's.
        Assert.Equal(200m, Assert.Single(lines, l => l.Code == TaxLineCodes.RealisedGain).Amount);
        Assert.Equal(1000m, Assert.Single(lines, l => l.Code == TaxLineCodes.RealisedGainUnknown).Amount);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me", new { pinel = Array.Empty<object>(), capitalLossesCarried = "150" });
        var result = JsonDocument.Parse(await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("result");
        Assert.Equal(("50.00", "0.00", "6.40"), (result.GetProperty("realisedGainsTaxable").GetString(),
            result.GetProperty("capitalLossesCarriedForward").GetString(), result.GetProperty("capitalFlatTax").GetString()));
    }
}
