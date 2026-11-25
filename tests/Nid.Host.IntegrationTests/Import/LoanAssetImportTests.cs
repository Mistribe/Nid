using System.Text.Json;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Nid.Host.IntegrationTests.Export;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>#188: the asset a loan finances travels in the file, as <c>asset</c>, resolved from the section <c>assets</c>.</summary>
public class LoanAssetImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Car = """
        {"id":"asset-car","name":"Voiture","kind":"vehicle","currency":"EUR","purchase_price":"15000.00","purchased_on":"2023-05-01","ownership":[{"person":"adult-1","share_bp":10000}]}
        """;

    private static string Loan(string extra = "\"asset\":\"asset-car\"") => $$"""
        [{"id":"loan-car","name":"Crédit auto","currency":"EUR","principal":{"amount":"12000.00","currency":"EUR"},
          "disbursed_on":"2024-04-10","first_payment_date":"2024-05-10","total_payments":60,{{extra}},
          "ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"shared"}]
        """;

    private const string Rate = """[{"id":"lp-car","loan":"loan-car","seq":1,"mode":"annuity","annual_rate":"0.04","rate_type":"fixed"}]""";

    private static Dictionary<string, string> File(string loans) => new()
    {
        ["persons"] = GoldenFile.Persons, ["assets"] = "[" + Car + "]", ["loans"] = loans, ["loan_phases"] = Rate,
    };

    [Fact]
    public async Task Round_trip_keeps_the_asset_a_loan_finances()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Anna");
        await RoundTripTests.CommitAsync(source, source.AdultA, File(Loan()));

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);
        Assert.Equal("asset-car", Assert.Single(first["loans"]).GetProperty("asset").GetString());

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Zoé");
        await RoundTripTests.CommitAsync(target, target.AdultA, RoundTripTests.AsFile(first));

        var list = await target.GetAsync<JsonElement>(target.AdultA, "/api/v1/assets");
        var asset = list.GetProperty("rows")[0].GetProperty("id").GetGuid();
        var detail = await target.GetAsync<JsonElement>(target.AdultA, $"/api/v1/assets/{asset}");
        Assert.Equal("Crédit auto", Assert.Single(detail.GetProperty("loans").EnumerateArray()).GetProperty("name").GetString());
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);
        Assert.Equal(JsonSerializer.Serialize(first["loans"]), JsonSerializer.Serialize(second["loans"]));
    }

    [Theory]
    [InlineData("\"asset\":\"asset-nope\"", "import.unresolved_reference")]
    [InlineData("\"asset\":\"asset-car\",\"envelope\":\"env-x\"", "realestate.invalid_asset")]
    public async Task A_loan_naming_an_unknown_asset_or_two_items_is_refused(string extra, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value });
        foreach (var (section, entities) in File(Loan(extra)))
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);

        var validated = await client.ValidateAsync(app.AdultA, session.Id, false);

        Assert.Equal([(code, "loans[0].asset")], validated.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)));
    }
}
