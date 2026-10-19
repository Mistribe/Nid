using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>Issue #34 §5: the sections <c>holdings</c>, <c>holding_snapshots</c> and <c>holding_movements</c>, asserted through the API.</summary>
[Collection("postgres")]
public class HoldingsImportTests(PostgresFixture fixture)
{
    private const string Envelope = """
        [{"id":"env-av","name":"Assurance-vie","kind":"life_insurance","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]}]
        """;

    private const string Holdings = """
        [{"id":"h-euro","item":"env-av","name":"Fonds euros","kind":"euro_fund"},
         {"id":"h-a","item":"env-av","name":"Fonds A","kind":"fund","isin":"FR0000120271","entry_cost_pct":"1.5"}]
        """;

    /// <summary>Fund A's line is 0.40 off units × price: a warning, never a refusal (holdings spec D6).</summary>
    private const string Snapshots = """
        [{"id":"s-2024","item":"env-av","as_of":"2024-12-31","lines":[
          {"holding":"h-euro","value":{"amount":"3000.00","currency":"EUR"}},
          {"holding":"h-a","units":"100","unit_price":"70","value":{"amount":"7000.40","currency":"EUR"}}]}]
        """;

    private const string Flows = """
        [{"id":"f-in","item":"env-av","occurred_on":"2024-01-01","kind":"contribution","amount":{"amount":"10000.00","currency":"EUR"}}]
        """;

    private const string Movements = """
        [{"id":"m-euro","holding":"h-euro","occurred_on":"2024-01-01","kind":"allocation_in","amount":{"amount":"3000.00","currency":"EUR"},"envelope_flow":"f-in"},
         {"id":"m-a","holding":"h-a","occurred_on":"2024-01-01","kind":"allocation_in","units":"100","unit_price":"70","amount":{"amount":"6000.00","currency":"EUR"}}]
        """;

    private static Dictionary<string, string> File(string holdings = Holdings, string snapshots = Snapshots, string movements = Movements) => new()
    {
        ["persons"] = GoldenFile.Persons, ["envelopes"] = Envelope, ["flows"] = Flows,
        ["holdings"] = holdings, ["holding_snapshots"] = snapshots, ["holding_movements"] = movements,
    };

    [Fact]
    public async Task Holdings_snapshots_and_movements_are_written_with_the_apis_warnings_and_a_second_import_changes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        var committed = await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File());

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["holdings"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["holding_snapshots"]);
        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report.Sections["holding_movements"]);
        Assert.Equal(
            [("investments.allocation_mismatch", "holding_movements[0]"), ("investments.holding_value_mismatch", "holding_snapshots[0].lines[1]")],
            committed.Report.Issues.Select(i => (i.Code, i.Path)).Order());
        Assert.All(committed.Report.Issues, i => Assert.Equal("warning", i.Severity));

        var url = $"/api/v1/investments/envelopes/{await EnvelopesImportTests.IdOf(app, "Assurance-vie")}";
        var read = await EnvelopesImportTests.JsonAsync(app, app.AdultA, $"{url}/holdings?on=2024-12-31");
        var fund = read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("name").GetString() == "Fonds A");
        Assert.Equal(("FR0000120271", "7000.4000"), (fund.GetProperty("isin").GetString(), fund.GetProperty("line").GetProperty("value").GetProperty("amount").GetString()));
        var euro = read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("name").GetString() == "Fonds euros").GetProperty("id").GetGuid();
        var flow = (await EnvelopesImportTests.JsonAsync(app, app.AdultA, url)).GetProperty("flows")[0].GetProperty("id").GetGuid();
        var movement = (await EnvelopesImportTests.JsonAsync(app, app.AdultA, $"{url}/holdings/{euro}/movements")).GetProperty("items")[0];
        Assert.Equal(flow, movement.GetProperty("envelopeFlowId").GetGuid());

        var again = await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File());
        foreach (var (section, count) in new[] { ("holdings", 2), ("holding_snapshots", 1), ("holding_movements", 2) })
            Assert.Equal(new CountsResponse(0, count, 0), again.Report!.Sections[section]);
    }

    [Fact]
    public async Task Other_content_under_a_known_id_is_a_conflict_and_replaced_only_with_replace()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, File());
        var renamed = File(holdings: Holdings.Replace("\"Fonds A\"", "\"Fonds A bis\""));

        var refused = await EnvelopesImportTests.ValidateAsync(client, app, app.AdultA, renamed);
        Assert.Equal("invalid", refused.State);
        Assert.Contains(refused.Report!.Issues, i => (i.Code, i.Path) == ("import.conflict", "holdings[1]"));
        await client.CancelRawAsync(app.AdultA, refused.Id);

        var replaced = await EnvelopesImportTests.CommitAsync(client, app, app.AdultA, renamed, replace: true);
        Assert.Equal(new CountsResponse(0, 1, 0, 1), replaced.Report!.Sections["holdings"]);
        var url = $"/api/v1/investments/envelopes/{await EnvelopesImportTests.IdOf(app, "Assurance-vie")}";
        Assert.Contains((await EnvelopesImportTests.JsonAsync(app, app.AdultA, $"{url}/holdings")).GetProperty("holdings").EnumerateArray(),
            h => h.GetProperty("name").GetString() == "Fonds A bis");
    }

    [Theory]
    [InlineData("""[{"id":"m","holding":"h-x","occurred_on":"2024-01-01","kind":"fee","amount":{"amount":"1.00","currency":"EUR"}}]""",
        "import.unresolved_reference", "holding_movements[0].holding")]
    [InlineData("""[{"id":"m","holding":"h-a","occurred_on":"2024-06-30","kind":"switch_out","units":"-1","amount":{"amount":"70.00","currency":"EUR"}}]""",
        "investments.invalid_movement", "holding_movements[0].switch_ref")]
    [InlineData("""[{"id":"m","holding":"h-euro","occurred_on":"2024-01-01","kind":"interest","units":"1","amount":{"amount":"1.00","currency":"EUR"}}]""",
        "investments.invalid_movement", "holding_movements[0].units")]
    [InlineData("""[{"id":"m","holding":"h-a","occurred_on":"2024-01-01","kind":"fee","units":"1","amount":{"amount":"1.00","currency":"EUR"}}]""",
        "investments.invalid_movement", "holding_movements[0].units")]
    [InlineData("""[{"id":"m","holding":"h-a","occurred_on":"2024-01-01","kind":"fee","amount":{"amount":"1.00","currency":"EUR"},"envelope_flow":"f-x"}]""",
        "import.unresolved_reference", "holding_movements[0].envelope_flow")]
    public async Task A_movement_is_refused_as_the_api_refuses_it(string movements, string code, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await EnvelopesImportTests.ValidateAsync(new ImportClient(app), app, app.AdultA, File(movements: movements));

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == (code, path));
    }

    [Theory]
    [InlineData("""[{"id":"h","item":"env-av","name":"X","kind":"fund","isin":"FR0000120272"}]""", "investments.invalid_isin", "holdings[0].isin")]
    [InlineData("""[{"id":"h","item":"env-x","name":"X","kind":"fund"}]""", "import.unresolved_reference", "holdings[0].item")]
    [InlineData("""[{"id":"h","item":"env-av","name":"X","kind":"gold"}]""", "investments.unknown_holding_kind", "holdings[0].kind")]
    [InlineData("""[{"id":"h1","item":"env-av","name":"X","kind":"fund","isin":"FR0000120271"},{"id":"h2","item":"env-av","name":"Y","kind":"fund","isin":"FR0000120271"}]""",
        "investments.invalid_holding", "holdings[1].isin")]
    public async Task A_holding_is_refused_as_the_api_refuses_it(string holdings, string code, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Contains(await IssuesAsync(app, File(holdings: holdings, snapshots: "[]", movements: "[]")), i => (i.Code, i.Path) == (code, path));
    }

    /// <summary>The refusals of a file: a batch's on receipt (the rules local to one entity), else the validation's.</summary>
    private static async Task<List<IssueResponse>> IssuesAsync(NidApp app, Dictionary<string, string> sections)
    {
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, EnvelopesImportTests.Adults(app));
        foreach (var (section, entities) in sections)
        {
            var batch = await client.PutAsync(app.AdultA, session.Id, section, 0, entities);
            if (!batch.Accepted)
                return batch.Issues;
        }
        var validated = await client.ValidateAsync(app.AdultA, session.Id);
        Assert.Equal("invalid", validated.State);
        return validated.Report!.Issues;
    }

    [Fact]
    public async Task Two_snapshots_of_an_envelope_on_one_date_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var twice = Snapshots.TrimEnd()[..^1] + """,{"id":"s-bis","item":"env-av","as_of":"2024-12-31","lines":[{"holding":"h-euro","value":{"amount":"1.00","currency":"EUR"}}]}]""";

        var validated = await EnvelopesImportTests.ValidateAsync(new ImportClient(app), app, app.AdultA, File(snapshots: twice));

        Assert.Equal("invalid", validated.State);
        Assert.Contains(validated.Report!.Issues, i => (i.Code, i.Path) == ("import.duplicate_fact", "holding_snapshots[1].as_of"));
    }
}
