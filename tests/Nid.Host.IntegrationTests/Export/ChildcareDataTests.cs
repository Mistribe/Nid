using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Host.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Export;

/// <summary>Childcare decisions 24–26: an arrangement and its validated months, exported then imported with the same figures.</summary>
public class ChildcareDataTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/childcare";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>An arrangement from 2025-09-01 (4 € net, Mon/Tue/Thu/Fri 08:00–17:30, 46 weeks) and its validated months.</summary>
    private static async Task<string> SourceAsync(NidApp app, string visibility = "shared", bool draftOctober = false, string? lateOn = null)
    {
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            name = "Garde de Léa", childId = app.Dependent.Value, carer = new { name = "Martine", pajemploi = "P1", approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "4", mealPrice = "3.50",
                programmedWeeks = 46, schedule = new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "17:30", meal = true }).ToArray(),
            },
            gaps = Array.Empty<object>(), visibility, owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var arrangement = $"{Url}/{(await JsonAsync(created)).GetProperty("id").GetGuid()}";
        await SaveAsync(app, $"{arrangement}/statements/2025-09", "2025-09-08", lateOn);
        await ValidateAsync(app, $"{arrangement}/statements/2025-09");
        await SaveAsync(app, $"{arrangement}/statements/2025-10", null);
        if (!draftOctober)
            await ValidateAsync(app, $"{arrangement}/statements/2025-10");
        return arrangement;
    }

    /// <summary>The month as read, a sick child on <paramref name="sick"/>, CMG 100.</summary>
    private static async Task SaveAsync(NidApp app, string url, string? sick, string? lateOn = null)
    {
        var read = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url));
        var days = read.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString() == sick
            ? new { date = sick, present = false, arrival = (string?)null, departure = (string?)null, meal = false, absence = (string?)"child_sick" }
            : new
            {
                date = d.GetProperty("date").GetString(), present = d.GetProperty("present").GetBoolean(), arrival = d.GetProperty("arrival").GetString(),
                departure = d.GetProperty("date").GetString() == lateOn ? "17:35" : d.GetProperty("departure").GetString(),
                meal = d.GetProperty("meal").GetBoolean(), absence = d.GetProperty("absence").GetString(),
            }).ToList();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, url,
            new { days, mileage = "0", cmg = "100", version = read.GetProperty("version").GetUInt32() })).StatusCode);
    }

    private static async Task ValidateAsync(NidApp app, string url)
    {
        var version = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, url))).GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/validate", new { version })).StatusCode);
    }

    private static async Task<Dictionary<string, List<JsonElement>>> ExportAsync(NidApp app, Nid.Core.Contracts.PersonId? reader = null)
        => await new ExportClient(app).ExportAllAsync(reader ?? app.AdultA);

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task<SessionResponse> RunAsync(NidApp app, Dictionary<string, string> file)
        => await new ImportClient(app).RunAsync(app.AdultA, Adults(app), file);

    private static IEnumerable<(string Code, string Path)> Errors(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path));

    /// <summary>The source's export as a file, its statements edited by <paramref name="edit"/>.</summary>
    private static Dictionary<string, string> FileOf(Dictionary<string, List<JsonElement>> export, Func<JsonArray, JsonArray>? edit = null)
    {
        var file = RoundTripTests.AsFile(export);
        if (edit is not null)
            file["childcare_statements"] = edit(JsonNode.Parse(file["childcare_statements"])!.AsArray()).ToJsonString();
        return file;
    }

    private static async Task<IReadOnlyList<JsonElement>> StatementsOfImportedAsync(NidApp app)
    {
        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}?scope=household&closed=true"));
        var id = list.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var months = new List<JsonElement>();
        foreach (var month in new[] { "2025-09", "2025-10" })
            months.Add(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}/statements/{month}")));
        return months;
    }

    [Fact]
    public async Task Childcare_round_trips_with_the_same_figures()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var export = await ExportAsync(source);
        Assert.Single(export["childcare_arrangements"]);
        Assert.Equal(2, export["childcare_statements"].Count);

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        await RoundTripTests.CommitAsync(target, target.AdultA, FileOf(export));

        var imported = await StatementsOfImportedAsync(target);
        Assert.All(imported, m => Assert.Equal("validated", m.GetProperty("state").GetString()));
        var again = await ExportAsync(target);
        Assert.Equal(export["childcare_statements"].Select(e => e.ToString()), again["childcare_statements"].Select(e => e.ToString()));
        Assert.Equal(export["childcare_arrangements"][0].GetProperty("terms").ToString(), again["childcare_arrangements"][0].GetProperty("terms").ToString());
    }

    [Fact]
    public async Task A_draft_month_is_not_exported()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SourceAsync(app, draftOctober: true);
        Assert.Equal(["2025-09"], (await ExportAsync(app))["childcare_statements"].Select(s => s.GetProperty("month").GetString()));
    }

    [Fact]
    public async Task The_other_adults_private_arrangement_is_not_exported()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SourceAsync(app, visibility: "private");
        var export = await ExportAsync(app, app.AdultB);
        Assert.Empty(export.GetValueOrDefault("childcare_arrangements") ?? []);
        Assert.Empty(export.GetValueOrDefault("childcare_statements") ?? []);
    }

    [Fact]
    public async Task Figures_that_differ_are_refused()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var file = FileOf(await ExportAsync(source), statements =>
        {
            statements[0]!["figures"]!["net_pay"] = "600.00";
            return statements;
        });

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        var session = await RunAsync(target, file);
        Assert.Contains(("childcare.figures_differ", "childcare_statements[0].figures"), Errors(session));
    }

    [Fact]
    public async Task Statements_out_of_order_apply_and_a_reimport_is_unchanged()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var file = FileOf(await ExportAsync(source), statements => new JsonArray(statements.Reverse().Select(s => s!.DeepClone()).ToArray()));

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        await RoundTripTests.CommitAsync(target, target.AdultA, file);
        var again = await RunAsync(target, file);
        Assert.Empty(Errors(again));
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["childcare_statements"]);
        Assert.Equal(new CountsResponse(0, 1, 0), again.Report!.Sections["childcare_arrangements"]);
    }

    [Fact]
    public async Task A_month_without_its_previous_one_is_refused()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var file = FileOf(await ExportAsync(source), statements => new JsonArray(statements[1]!.DeepClone()));

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        Assert.Contains(("childcare.previous_not_validated", "childcare_statements[0].month"), Errors(await RunAsync(target, file)));
    }

    [Fact]
    public async Task An_adult_as_child_is_refused()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var file = RoundTripTests.AsFile(await ExportAsync(source));
        var arrangements = JsonNode.Parse(file["childcare_arrangements"])!.AsArray();
        arrangements[0]!["child"] = "adult-1";
        file["childcare_arrangements"] = arrangements.ToJsonString();

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        Assert.Contains(("childcare.child_not_dependent", "childcare_arrangements[0].child"), Errors(await RunAsync(target, file)));
    }

    [Fact]
    public async Task The_edit_level_round_trips_and_a_private_one_is_refused()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var file = RoundTripTests.AsFile(await ExportAsync(source));
        var arrangements = JsonNode.Parse(file["childcare_arrangements"])!.AsArray();
        arrangements[0]!["share_level"] = "edit";
        file["childcare_arrangements"] = arrangements.ToJsonString();

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        await RoundTripTests.CommitAsync(target, target.AdultA, file);
        Assert.Equal("edit", (await ExportAsync(target))["childcare_arrangements"][0].GetProperty("share_level").GetString());

        var client = new ImportClient(target);
        var session = await client.StartAsync(target.AdultA, Adults(target));
        var batch = await client.PutRawAsync(target.AdultA, session.Id, "childcare_arrangements", 0,
            Arrangement("""[{"from":"2025-09-01","net_hourly_rate":"4","overtime_pct":"10","upkeep_per_9h":"4","programmed_weeks":46,"schedule":[]}]""")
                .Replace("\"ownership\"", "\"visibility\":\"private\",\"share_level\":\"edit\",\"ownership\""));
        var body = await batch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("childcare_arrangements[0].share_level", body);
        Assert.Contains("A private item has no sharing level.", body);
    }

    [Fact]
    public async Task Five_minutes_more_reimports_unchanged()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source, lateOn: "2025-09-02"); // 0.0833… complementary hours, stored as 0.08
        var file = FileOf(await ExportAsync(source));

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        await RoundTripTests.CommitAsync(target, target.AdultA, file);
        var again = await RunAsync(target, file);
        Assert.Empty(Errors(again));
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["childcare_statements"]);
    }

    private const string Persons = """
        [{"ref":"adult-1","kind":"adult","display_name":"Adulte 1"},{"ref":"adult-2","kind":"adult","display_name":"Adulte 2"},
         {"ref":"dependent-1","kind":"dependent","display_name":"Léa"}]
        """;

    private static string Arrangement(string terms, string gaps = "[]") => $$"""
        [{"id":"cc-1","name":"Garde de Léa","child":"dependent-1","carer":{"name":"Martine"},"terms":{{terms}},"gaps":{{gaps}},
          "ownership":[{"person":"adult-1","share_bp":10000}]}]
        """;

    private const string Monday = """[{"day":1,"arrival":"08:00","departure":"17:30","meal":false}]""";

    [Fact]
    public async Task Each_contract_year_keeps_the_weeks_then_in_force()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(app, "Personne B");
        // Year 1: 40 weeks and 12 weeks without care; year 2: 46 weeks and none.
        var file = new Dictionary<string, string>
        {
            ["persons"] = Persons,
            ["childcare_arrangements"] = Arrangement($$"""
                [{"from":"2025-09-01","to":"2026-08-31","net_hourly_rate":"4","overtime_pct":"10","upkeep_per_9h":"4","programmed_weeks":40,"schedule":{{Monday}}},
                 {"from":"2026-09-01","net_hourly_rate":"4","overtime_pct":"10","upkeep_per_9h":"4","programmed_weeks":46,"schedule":{{Monday}}}]
                """, """[{"monday":"2026-02-02","weeks":12}]"""),
        };
        Assert.Empty(Errors(await RunAsync(app, file)));
    }

    [Fact]
    public async Task Replacing_with_an_earlier_end_than_a_validated_month_is_refused()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var file = FileOf(await ExportAsync(source));

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        await RoundTripTests.CommitAsync(target, target.AdultA, file);

        var arrangements = JsonNode.Parse(file["childcare_arrangements"])!.AsArray();
        arrangements[0]!["terms"]![0]!["to"] = "2025-09-30";
        arrangements[0]!["closed"] = true;
        file["childcare_arrangements"] = arrangements.ToJsonString();
        file["childcare_statements"] = new JsonArray(JsonNode.Parse(file["childcare_statements"])!.AsArray()[0]!.DeepClone()).ToJsonString();
        var replaced = await new ImportClient(target).RunAsync(target.AdultA, Adults(target), file, replace: true);
        Assert.Contains(("childcare.end_before_validated", "childcare_arrangements[0].terms"), Errors(replaced));
    }

    [Fact]
    public async Task A_null_schedule_slot_is_an_issue_not_a_crash()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));
        var batch = await client.PutRawAsync(app.AdultA, session.Id, "childcare_arrangements", 0,
            Arrangement("""[{"from":"2025-09-01","net_hourly_rate":"4","overtime_pct":"10","upkeep_per_9h":"4","programmed_weeks":46,"schedule":[null]}]"""));
        Assert.NotEqual(HttpStatusCode.InternalServerError, batch.StatusCode);
        Assert.Contains("childcare_arrangements[0].terms[0].schedule", await batch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
