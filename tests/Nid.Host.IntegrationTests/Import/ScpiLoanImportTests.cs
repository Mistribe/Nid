using Nid.TestSupport;
using static Nid.Host.IntegrationTests.Import.EnvelopesImportTests;
using static Nid.Host.IntegrationTests.Import.EstateFile;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>#180: a file's loan finances one of its SCPI envelopes, and an import never strips a financed SCPI's kind.</summary>
public class ScpiLoanImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Envelope(string kind) => $$"""
        [{"id":"env-scpi","name":"SCPI Bureaux","kind":"{{kind}}","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]}]
        """;

    private const string ScpiLoan = """
        {"id":"loan-scpi","name":"Prêt SCPI","currency":"EUR","principal":{"amount":"4000.00","currency":"EUR"},
         "disbursed_on":"2024-04-10","first_payment_date":"2024-05-10","total_payments":120,"envelope":"env-scpi",
         "ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"shared"}
        """;

    private const string ScpiRate = """{"id":"lp-scpi","loan":"loan-scpi","seq":1,"mode":"annuity","annual_rate":"0.035","rate_type":"fixed"}""";

    private static Dictionary<string, string> WithLoan(string kind)
    {
        var file = File(Envelope(kind));
        file["loans"] = List(ScpiLoan);
        file["loan_phases"] = List(ScpiRate);
        return file;
    }

    [Fact]
    public async Task A_loan_naming_an_envelope_that_is_not_an_scpi_is_refused_at_validation()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File(Envelope("savings")));

        var validated = await ValidateAsync(client, app, app.AdultA, WithLoan("savings"));

        Assert.Equal("invalid", validated.State);
        var refused = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal(("realestate.invalid_envelope", "loans[0].envelope"), (refused.Code, refused.Path));
    }

    [Fact]
    public async Task A_replace_keeps_a_financed_scpi_an_scpi()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var committed = await CommitAsync(client, app, app.AdultA, WithLoan("scpi"));
        Assert.Equal("committed", committed.State);

        var validated = await ValidateAsync(client, app, app.AdultA, File(Envelope("savings")), replace: true);

        Assert.Equal("invalid", validated.State);
        var refused = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal(("investments.envelope_financed", "envelopes[0].kind"), (refused.Code, refused.Path));
    }
}
