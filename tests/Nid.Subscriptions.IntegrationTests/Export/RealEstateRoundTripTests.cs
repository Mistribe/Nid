using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Subscriptions.IntegrationTests.Import;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Import.EnvelopesImportTests;

namespace Nid.Subscriptions.IntegrationTests.Export;

/// <summary>Spec 3F1 §4 and §6: a household's real estate, exported, imported into a fresh instance, exports the same again.</summary>
[Collection("postgres")]
public class RealEstateRoundTripTests(PostgresFixture fixture)
{
    /// <summary>The file carries the adults' names: the same in both instances.</summary>
    private static async Task NameAdultsAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        (await core.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).DisplayName = "Adulte 1";
        (await core.Persons.SingleAsync(p => p.Id == app.AdultB.Value)).DisplayName = "Adulte 2";
        await core.SaveChangesAsync();
    }

    private static Dictionary<string, string> AsFile(Dictionary<string, List<JsonElement>> export)
        => export.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => JsonSerializer.Serialize(s.Value));

    private static string Canonical(Dictionary<string, List<JsonElement>> export)
        => JsonSerializer.Serialize(export.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key, s => s.Value));

    private static async Task PostAsync(NidApp app, string url, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_households_real_estate_imported_into_a_fresh_instance_exports_the_same_again()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await NameAdultsAsync(source);
        // Two properties, two loans (one zero-rate on the same home, the main one with its bank's table as reference), phases, an insurance, a fee, an early repayment, planned
        // costs, tenancies, an estimate and flows.
        var file = EstateFile.Loans(loans: EstateFile.List(EstateFile.BankLoan, EstateFile.ZeroLoan),
            bank: EstateFile.List(EstateFile.Bank1, EstateFile.Bank2, EstateFile.Bank3));
        file["valuations"] = """[{"id":"v-home","item":"prop-home","as_of":"2024-12-31","amount":{"amount":"300000","currency":"EUR"}}]""";
        file["flows"] = """
            [{"id":"f-works","item":"prop-home","occurred_on":"2021-05-10","kind":"expense","amount":{"amount":"12000","currency":"EUR"},"category":"works"},
             {"id":"f-rent","item":"prop-flat","occurred_on":"2025-09-05","kind":"income","amount":{"amount":"750","currency":"EUR"},"category":"rent"}]
            """;
        await CommitAsync(new ImportClient(source), source, source.AdultA, file);
        // What the app adds on top, none of it with a file id: a tenancy, an insured expense, an early repayment with its penalty.
        var home = await PropertiesImportTests.PropertyIdAsync(source, "Maison");
        var main = (await JsonAsync(source, source.AdultA, "/api/v1/realestate/loans?scope=me")).GetProperty("items").EnumerateArray()
            .Single(l => l.GetProperty("name").GetString() == "Prêt principal").GetProperty("id").GetGuid();
        await PostAsync(source, $"/api/v1/realestate/properties/{home}/tenancies",
            new { label = "Chambre", startsOn = "2023-01-01", endsOn = "2023-07-01", rentMonthly = "400" });
        await PostAsync(source, $"/api/v1/realestate/properties/{home}/flows",
            new { occurredOn = "2025-02-01", kind = "expense", amount = "800", category = "insurance" });
        await PostAsync(source, $"/api/v1/realestate/loans/{main}/repayments",
            new { onDate = "2024-03-15", amount = "5000", effect = "reduce_payment", penalty = "150" });

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);

        await using var target = await NidApp.StartAsync(fixture);
        await NameAdultsAsync(target);
        await CommitAsync(new ImportClient(target), target, target.AdultA, AsFile(first));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(Canonical(first), Canonical(second));
        // Not vacuous: every section carries what the household holds, the app's rows under derived ids.
        Assert.Equal((2, 3, 2, 3), (first["properties"].Count, first["tenancies"].Count, first["property_recurring_costs"].Count, first["flows"].Count));
        Assert.Equal((2, 3, 1, 2, 2, 1), (first["loans"].Count, first["loan_phases"].Count, first["loan_insurances"].Count,
            first["loan_fees"].Count, first["loan_events"].Count, first["valuations"].Count));
        Assert.Contains(first["loan_fees"], f => f.GetProperty("kind").GetString() == "early_repayment_penalty");
        Assert.Equal(3, first["loan_bank_schedule"].Count);
        Assert.Contains(first["loans"], l => l.GetProperty("id").GetString() == "loan-main" && l.GetProperty("schedule_source").GetString() == "bank");
        Assert.Contains(first["tenancies"], t => t.GetProperty("label").GetString() == "Chambre" && t.GetProperty("id").GetString() is not ("ten-1" or "ten-2"));
        Assert.Contains(first["loans"], l => l.GetProperty("id").GetString() == "loan-ptz" && l.GetProperty("property").GetString() == "prop-home");
    }
}
