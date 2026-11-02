using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.IntegrationTests.Export;
using Nid.Investments.Contracts;
using NodaTime;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>Holdings exposure spec D9 and C4: the section <c>security_breakdowns</c>, asserted through the API.</summary>
public class BreakdownsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Envelope = """
        [{"id":"env-av","name":"Assurance-vie","kind":"life_insurance","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]}]
        """;

    private const string Holdings = """
        [{"id":"h-euro","item":"env-av","name":"Fonds euros","kind":"euro_fund","isin":"FR0000120628"},
         {"id":"h-a","item":"env-av","name":"Fonds A","kind":"fund","isin":"FR0000120271"}]
        """;

    private const string Breakdowns = """
        [{"isin":"FR0000120271","as_of":"2024-06-30","regions":[{"code":"europe_ex_france","pct":"60.5"},{"code":"north_america","pct":"39.5"}]},
         {"isin":"FR0000120271","as_of":"2025-06-30","regions":[{"code":"france","pct":"100"}],"sectors":[{"code":"financials","pct":"100"}]}]
        """;

    private static Dictionary<string, string> File(string breakdowns = Breakdowns, string holdings = Holdings) => new()
    {
        ["persons"] = GoldenFile.Persons, ["envelopes"] = Envelope, ["holdings"] = holdings, ["security_breakdowns"] = breakdowns,
    };

    private static Task<SessionResponse> ValidateAsync(NidApp app, string breakdowns, string holdings = Holdings)
        => EnvelopesImportTests.ValidateAsync(new ImportClient(app), app, app.AdultA, File(breakdowns, holdings));

    [Fact]
    public async Task An_export_carries_the_breakdowns_and_an_import_restores_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        var committed = await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File());
        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["security_breakdowns"]);

        var exported = (await new ExportClient(app).ExportAllAsync(app.AdultA))["security_breakdowns"];
        Assert.Equal(["2024-06-30", "2025-06-30"], exported.Select(b => b.GetProperty("as_of").GetString()));
        Assert.All(exported, b => Assert.False(b.TryGetProperty("document_id", out _)));
        Assert.False(exported[0].TryGetProperty("sectors", out _));
        Assert.Equal("financials", exported[1].GetProperty("sectors")[0].GetProperty("code").GetString());

        var again = await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File(JsonSerializer.Serialize(exported)));
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["security_breakdowns"]);
    }

    [Fact]
    public async Task An_imported_breakdown_that_is_not_100_is_refused_at_its_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var bad = """[{"isin":"FR0000120271","as_of":"2024-06-30","regions":[{"code":"france","pct":"60"}]}]""";

        var validated = await ValidateAsync(app, bad);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == ("investments.breakdown_not_100", "security_breakdowns[0].regions"));
    }

    [Fact]
    public async Task An_unknown_code_and_an_empty_list_are_refused_before_anything_is_stored()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var bad = """
            [{"isin":"FR0000120271","as_of":"2024-06-30","regions":[{"code":"mars","pct":"100"}]},
             {"isin":"FR0000120271","as_of":"2024-07-31","regions":[]}]
            """;

        var validated = await ValidateAsync(app, bad);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == ("request.invalid", "security_breakdowns[0].regions[0].code"));
        Assert.Contains(validated.Report.Issues, i => (i.Code, i.Path) == ("investments.breakdown_not_100", "security_breakdowns[1].regions"));
    }

    [Fact]
    public async Task An_isin_is_matched_and_stored_trimmed_and_upper_case_like_a_holdings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var holdings = """[{"id":"h-a","item":"env-av","name":"Fonds A","kind":"fund","isin":"fr0000120271"}]""";
        var breakdowns = """[{"isin":" fr0000120271","as_of":"2024-06-30","regions":[{"code":"france","pct":"100"}]}]""";

        var committed = await EnvelopesImportTests.CommitAsync(new ImportClient(app), app, app.AdultA, File(breakdowns, holdings));

        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report!.Sections["security_breakdowns"]);
        await using var read = app.Services.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>().AllAsync(["FR0000120271"], TestContext.Current.CancellationToken);
        Assert.Equal("FR0000120271", stored.Single().Isin);
    }

    [Fact]
    public async Task A_breakdown_of_an_isin_no_holding_of_the_file_holds_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var other = """[{"isin":"FR0000131104","as_of":"2024-06-30","regions":[{"code":"france","pct":"100"}]}]""";

        var validated = await ValidateAsync(app, other);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == ("request.invalid", "security_breakdowns[0].isin"));
    }

    [Fact]
    public async Task A_breakdown_of_a_euro_fund_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var euro = """[{"isin":"FR0000120628","as_of":"2024-06-30","regions":[{"code":"france","pct":"100"}]}]""";

        var validated = await ValidateAsync(app, euro);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == ("investments.breakdown_not_market", "security_breakdowns[0].isin"));
    }

    [Fact]
    public async Task A_breakdown_of_the_same_isin_and_date_with_other_values_replaces_it_and_drops_its_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File());
        var ct = TestContext.Current.CancellationToken;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>();
            var stored = (await store.AllAsync(["FR0000120271"], ct)).First();
            await store.WriteAsync(stored with { DocumentId = Guid.NewGuid() }, ct);
        }

        var changed = """[{"isin":"FR0000120271","as_of":"2024-06-30","regions":[{"code":"japan","pct":"100"}]}]""";
        var replaced = await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File(changed));

        Assert.Equal(new CountsResponse(0, 0, 0, 1), replaced.Report!.Sections["security_breakdowns"]);
        await using var read = app.Services.CreateAsyncScope();
        var versions = await read.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>().AllAsync(["FR0000120271"], ct);
        var version = versions.Single(v => v.AsOf == new LocalDate(2024, 6, 30));
        Assert.Equal(("japan", null), (version.Regions.Single().Code, version.DocumentId));
    }

    [Fact]
    public async Task A_breakdown_of_the_same_isin_and_date_twice_in_the_file_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var twice = """
            [{"isin":"FR0000120271","as_of":"2024-06-30","regions":[{"code":"france","pct":"100"}]},
             {"isin":"FR0000120271","as_of":"2024-06-30","regions":[{"code":"japan","pct":"100"}]}]
            """;

        var validated = await ValidateAsync(app, twice);

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == ("import.duplicate_fact", "security_breakdowns[1].as_of"));
    }
}
