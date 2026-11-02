using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.Host.IntegrationTests.Import.EnvelopesImportTests;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>2C1 spec §5: the <c>income_sources</c> and <c>income_phases</c> sections, asserted through the real income API.</summary>
public class IncomeImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Salary = """
        {"id":"inc-salary-1","name":"Salaire","kind":"salary","payer_label":"Employeur","currency":"EUR",
         "ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"shared"}
        """;

    /// <summary>A fixed-term contract, ended; co-owned so that adult-1 may import it (an item is imported by one of its owners).</summary>
    private const string Cdd = """
        {"id":"inc-cdd","name":"CDD","kind":"salary","currency":"EUR","ended_on":"2025-06-30",
         "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}]}
        """;

    private const string Ip1 = """
        {"id":"ip-1","source":"inc-salary-1","label":"Salaire de base","starts_on":"2024-09-01","ends_on":"2025-09-01",
         "net_amount":{"amount":"2200.00","currency":"EUR"},"every":{"count":1,"unit":"month"}}
        """;

    private const string Ip2 = """
        {"id":"ip-2","source":"inc-salary-1","label":"Salaire de base","starts_on":"2025-09-01",
         "net_amount":{"amount":"2300.00","currency":"EUR"},"every":{"count":1,"unit":"month"}}
        """;

    private const string Ip3 = """
        {"id":"ip-3","source":"inc-salary-1","label":"Prime annuelle","starts_on":"2024-12-15",
         "net_amount":{"amount":"1800.00","currency":"EUR"},"every":{"count":1,"unit":"year"}}
        """;

    private const string IpCdd = """
        {"id":"ip-cdd","source":"inc-cdd","label":"Salaire de base","starts_on":"2025-01-01","ends_on":"2025-06-30",
         "net_amount":{"amount":"1500.00","currency":"EUR"},"every":{"count":1,"unit":"month"}}
        """;

    private const string Bonus = """
        [{"id":"f-bonus","item":"inc-salary-1","occurred_on":"2025-03-10","kind":"income","amount":{"amount":"500","currency":"EUR"},"category":"one_off"}]
        """;

    internal static string List(params string[] entities) => "[" + string.Join(",", entities) + "]";

    internal static Dictionary<string, string> IncomeFile(string? sources = null, string? phases = null, bool flows = true)
    {
        var file = new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["income_sources"] = sources ?? List(Salary, Cdd),
            ["income_phases"] = phases ?? List(Ip1, Ip2, Ip3, IpCdd),
        };
        if (flows)
            file["flows"] = Bonus;
        return file;
    }

    private static async Task<List<JsonElement>> SourcesAsync(NidApp app, PersonId reader, bool archived)
        => (await JsonAsync(app, reader, "/api/v1/income/sources?scope=me" + (archived ? "&archived=true" : "")))
            .GetProperty("items").EnumerateArray().ToList();

    private static async Task<Guid> SourceIdAsync(NidApp app, string name)
        => (await SourcesAsync(app, app.AdultA, archived: true)).Single(s => s.GetProperty("displayName").GetString() == name).GetProperty("id").GetGuid();

    private static async Task<JsonElement> DetailAsync(NidApp app, string name)
        => await JsonAsync(app, app.AdultA, $"/api/v1/income/sources/{await SourceIdAsync(app, name)}");

    private static decimal Amount(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<List<(string? Ref, string Label, LocalDate StartsOn, LocalDate? EndsOn, decimal Net)>> StoredPhasesAsync(NidApp app, Guid source)
    {
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        return (await income.Phases.AsNoTracking().Where(p => p.SourceItemId == source).ToListAsync())
            .Select(p => (p.ImportRef, p.Label, p.StartsOn, p.EndsOn, p.NetAmount))
            .OrderBy(p => p.StartsOn).ThenBy(p => p.Label, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<(string Code, string Path)> ErrorsOf(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path));

    [Fact]
    public async Task Sources_phases_and_a_one_off_are_created()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, IncomeFile());

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["income_sources"]);
        Assert.Equal(new CountsResponse(4, 0, 0), committed.Report.Sections["income_phases"]);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report.Sections["flows"]);
        var open = await SourcesAsync(app, app.AdultA, archived: false);
        var salary = Assert.Single(open);
        Assert.Equal("Salaire", salary.GetProperty("displayName").GetString());
        Assert.Equal("2450.00", salary.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
        Assert.Equal("Employeur", salary.GetProperty("payerLabel").GetString());
        var cdd = (await SourcesAsync(app, app.AdultA, archived: true)).Single(s => s.GetProperty("displayName").GetString() == "CDD");
        Assert.True(cdd.GetProperty("ended").GetBoolean());
        Assert.Equal("2025-06-30", cdd.GetProperty("endedOn").GetString());

        var detail = await DetailAsync(app, "Salaire");
        Assert.Equal(3, detail.GetProperty("phases").GetArrayLength());
        var oneOff = Assert.Single(detail.GetProperty("oneOffs").EnumerateArray());
        Assert.Equal("2025-03-10", oneOff.GetProperty("occurredOn").GetString());
        Assert.Equal(500m, Amount(oneOff.GetProperty("amount")));
        var refs = (await StoredPhasesAsync(app, await SourceIdAsync(app, "Salaire"))).Select(p => p.Ref);
        Assert.Equal(["ip-1", "ip-3", "ip-2"], refs);
        // The co-owner reads the shared, ended one.
        Assert.Contains(await SourcesAsync(app, app.AdultB, archived: true), s => s.GetProperty("displayName").GetString() == "CDD");
    }

    [Fact]
    public async Task Phase_taxable_amount_round_trips()
    {
        const string Taxed = """
            {"id":"ip-t","source":"inc-salary-1","label":"Salaire de base","starts_on":"2025-01-01",
             "net_amount":{"amount":"3950.00","currency":"EUR"},"every":{"count":1,"unit":"month"},
             "gross_amount":{"amount":"5500.00","currency":"EUR"},"taxable_amount":{"amount":"4400.00","currency":"EUR"}}
            """;
        await using var source = await NidApp.StartAsync(fixture);
        await CommitAsync(new ImportClient(source), source, source.AdultA, IncomeFile(sources: List(Salary), phases: List(Taxed), flows: false));
        var first = await new Export.ExportClient(source).ExportAllAsync(source.AdultA);

        await using var target = await NidApp.StartAsync(fixture);
        await Export.RoundTripTests.CommitAsync(target, target.AdultA, Export.RoundTripTests.AsFile(first));
        var second = await new Export.ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(4400.00m, Amount(Assert.Single(first["income_phases"]).GetProperty("taxable_amount")));
        Assert.Equal(Export.RoundTripTests.Canonical(first), Export.RoundTripTests.Canonical(second));
        var phase = Assert.Single((await DetailAsync(target, "Salaire")).GetProperty("phases").EnumerateArray());
        Assert.Equal(4400.00m, Amount(phase.GetProperty("taxableAmount")));
    }

    [Fact]
    public async Task One_off_taxable_survives_an_export_import_round_trip()
    {
        const string Taxed = """
            [{"id":"f-bonus","item":"inc-salary-1","occurred_on":"2025-03-10","kind":"income","amount":{"amount":"4812.50","currency":"EUR"},"category":"one_off"}]
            """;
        await using var source = await NidApp.StartAsync(fixture);
        var file = IncomeFile(sources: List(Salary), phases: List(Ip1), flows: false);
        file["flows"] = Taxed;
        file["income_one_off_taxable"] = """[{"flow_id":"f-bonus","taxable_amount":{"amount":"5150.00","currency":"EUR"}}]""";
        var committed = await CommitAsync(new ImportClient(source), source, source.AdultA, file);
        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report!.Sections["income_one_off_taxable"]);
        Assert.Equal(5150.00m, Amount(Assert.Single((await DetailAsync(source, "Salaire")).GetProperty("oneOffs").EnumerateArray()).GetProperty("taxableAmount")));
        var first = await new Export.ExportClient(source).ExportAllAsync(source.AdultA);
        Assert.Equal(5150.00m, Amount(Assert.Single(first["income_one_off_taxable"]).GetProperty("taxable_amount")));

        await using var target = await NidApp.StartAsync(fixture);
        await Export.RoundTripTests.CommitAsync(target, target.AdultA, Export.RoundTripTests.AsFile(first));
        var second = await new Export.ExportClient(target).ExportAllAsync(target.AdultA);

        Assert.Equal(Export.RoundTripTests.Canonical(first), Export.RoundTripTests.Canonical(second));
        Assert.Equal(5150.00m, Amount(Assert.Single((await DetailAsync(target, "Salaire")).GetProperty("oneOffs").EnumerateArray()).GetProperty("taxableAmount")));
        await using var income = PostgresFixture.NewIncomeContext(target.ConnectionString);
        Assert.Equal(1, await income.Database.SqlQuery<int>($"select count(*)::int as \"Value\" from income.one_off_taxable").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static Dictionary<string, string> TaxedFile(string source, string phase, string item, string amount, string? taxable)
    {
        var file = IncomeFile(sources: List(source), phases: List(phase), flows: false);
        file["flows"] = List($$$"""{"id":"f-t","item":"{{{item}}}","occurred_on":"2025-03-10","kind":"income","amount":{"amount":"{{{amount}}}","currency":"EUR"},"category":"one_off"}""");
        if (taxable is not null)
            file["income_one_off_taxable"] = List($$$"""{"flow_id":"f-t","taxable_amount":{"amount":"{{{taxable}}}","currency":"EUR"}}""");
        return file;
    }

    private static async Task<List<OneOffTaxableRow>> TaxableRowsAsync(NidApp app)
    {
        await using var income = PostgresFixture.NewIncomeContext(app.ConnectionString);
        return await income.OneOffTaxables.AsNoTracking().ToListAsync();
    }

    private static async Task<JsonElement> OneOffOfAsync(NidApp app, string source)
        => Assert.Single((await DetailAsync(app, source)).GetProperty("oneOffs").EnumerateArray());

    [Fact]
    public async Task A_corrected_one_off_takes_the_taxable_amount_of_the_file_and_leaves_none_on_the_retired_flow()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, TaxedFile(Cdd, IpCdd, "inc-cdd", "500", "600"));
        var before = Assert.Single(await TaxableRowsAsync(app));

        var replaced = await CommitAsync(client, app, app.AdultA, TaxedFile(Cdd, IpCdd, "inc-cdd", "700", "650"), replace: true);

        Assert.Equal(new CountsResponse(0, 0, 0, 1), replaced.Report!.Sections["flows"]);
        Assert.Equal(new CountsResponse(1, 0, 0), replaced.Report.Sections["income_one_off_taxable"]);
        var oneOff = await OneOffOfAsync(app, "CDD");
        var row = Assert.Single(await TaxableRowsAsync(app));
        Assert.NotEqual(before.FlowId, row.FlowId);
        Assert.Equal(oneOff.GetProperty("id").GetGuid(), row.FlowId);
        Assert.Equal(650m, Amount(oneOff.GetProperty("taxableAmount")));
    }

    [Fact]
    public async Task A_corrected_one_off_without_an_entry_loses_its_taxable_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, TaxedFile(Cdd, IpCdd, "inc-cdd", "500", "600"));

        await CommitAsync(client, app, app.AdultA, TaxedFile(Cdd, IpCdd, "inc-cdd", "700", null), replace: true);

        Assert.Empty(await TaxableRowsAsync(app));
        Assert.Equal(JsonValueKind.Null, (await OneOffOfAsync(app, "CDD")).GetProperty("taxableAmount").ValueKind);
    }

    [Fact]
    public async Task Another_adult_importing_the_same_file_again_finds_the_flow_as_the_Core_does()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var file = TaxedFile(Cdd, IpCdd, "inc-cdd", "500", "600");
        await CommitAsync(client, app, app.AdultA, file);

        var again = await CommitAsync(client, app, app.AdultB, file);

        Assert.Equal(new CountsResponse(0, 1, 0), again.Report!.Sections["flows"]);
        Assert.Equal(new CountsResponse(0, 1, 0), again.Report.Sections["income_one_off_taxable"]);
        Assert.Equal(600m, Amount((await OneOffOfAsync(app, "CDD")).GetProperty("taxableAmount")));
        Assert.Single(await TaxableRowsAsync(app));
    }

    [Fact]
    public async Task Another_adult_correcting_the_one_off_leaves_no_orphan_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, TaxedFile(Cdd, IpCdd, "inc-cdd", "500", "600"));

        await CommitAsync(client, app, app.AdultB, TaxedFile(Cdd, IpCdd, "inc-cdd", "700", "650"), replace: true);

        var oneOff = await OneOffOfAsync(app, "CDD");
        var row = Assert.Single(await TaxableRowsAsync(app));
        Assert.Equal(oneOff.GetProperty("id").GetGuid(), row.FlowId);
        Assert.Equal(650m, row.TaxableAmount);
    }

    [Fact]
    public async Task A_taxable_amount_on_a_source_the_importer_cannot_change_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, TaxedFile(Salary, Ip1, "inc-salary-1", "500", "600"));

        // Adult-2 reads the salary only: the source and the flow are as stored, the taxable amount is not.
        var refused = await ValidateAsync(client, app, app.AdultB, TaxedFile(Salary, Ip1, "inc-salary-1", "500", "650"), replace: true);

        Assert.Equal([(ImportCodes.NotPermitted, "income_one_off_taxable[0]")], ErrorsOf(refused));
        await client.CancelRawAsync(app.AdultB, refused.Id);
    }

    [Fact]
    public async Task A_taxable_amount_on_a_flow_of_another_module_is_an_import_issue()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = TaxedFile(Salary, Ip1, "inc-salary-1", "500", "600");
        file["flows"] = List("""{"id":"f-t","item":"ghost","occurred_on":"2025-03-10","kind":"income","amount":{"amount":"500","currency":"EUR"},"category":"one_off"}""");

        var validated = await ValidateAsync(new ImportClient(app), app, app.AdultA, file);

        Assert.Contains((ImportCodes.InvalidValue, "income_one_off_taxable[0].flow_id"), ErrorsOf(validated));
    }

    [Fact]
    public async Task A_taxable_amount_on_an_unknown_flow_is_an_import_issue()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = IncomeFile(sources: List(Salary), phases: List(Ip1));
        file["income_one_off_taxable"] = """[{"flow_id":"f-nobody","taxable_amount":{"amount":"10","currency":"EUR"}}]""";

        var validated = await ValidateAsync(new ImportClient(app), app, app.AdultA, file);

        Assert.Equal([(ImportCodes.UnresolvedReference, "income_one_off_taxable[0].flow_id")], ErrorsOf(validated));
    }

    [Fact]
    public async Task The_same_file_again_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, IncomeFile());

        var again = await CommitAsync(client, app, app.AdultA, IncomeFile());

        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["income_sources"]);
        Assert.Equal(new CountsResponse(0, 4, 0), again.Report.Sections["income_phases"]);
        Assert.Equal(new CountsResponse(0, 1, 0), again.Report.Sections["flows"]);
        Assert.Empty(again.Report.Issues);
        Assert.Equal(2, (await SourcesAsync(app, app.AdultA, archived: true)).Count);
        Assert.Equal(3, (await StoredPhasesAsync(app, await SourceIdAsync(app, "Salaire"))).Count);
    }

    [Fact]
    public async Task Other_content_is_a_conflict_and_replace_updates_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, IncomeFile());
        var salary = await SourceIdAsync(app, "Salaire");
        var raised = IncomeFile(phases: List(Ip1, Ip2.Replace("2300.00", "2400.00"), Ip3, IpCdd));

        var validated = await ValidateAsync(client, app, app.AdultA, raised);

        Assert.Equal("invalid", validated.State);
        Assert.Equal([(ImportCodes.Conflict, "income_phases[1]")], ErrorsOf(validated));
        Assert.Equal(new CountsResponse(0, 3, 1), validated.Report!.Sections["income_phases"]);
        await client.CancelRawAsync(app.AdultA, validated.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, raised, replace: true);

        Assert.Equal(new CountsResponse(0, 3, 0, 1), replaced.Report!.Sections["income_phases"]);
        Assert.Equal(new CountsResponse(0, 2, 0), replaced.Report.Sections["income_sources"]);
        var stored = await StoredPhasesAsync(app, salary);
        Assert.Equal(3, stored.Count);
        Assert.Equal(2400m, stored.Single(p => p.Ref == "ip-2").Net);
        var detail = await DetailAsync(app, "Salaire");
        Assert.Equal("2550.00", detail.GetProperty("monthlyEquivalent").GetProperty("amount").GetString());
        Assert.Contains(detail.GetProperty("phases").EnumerateArray(), p => Amount(p.GetProperty("netAmount")) == 2400m);

        // A phase stays on its source, whatever the mode.
        var moved = await ValidateAsync(client, app, app.AdultA,
            IncomeFile(phases: List(Ip1, Ip2, Ip3.Replace("\"source\":\"inc-salary-1\"", "\"source\":\"inc-cdd\""), IpCdd)), replace: true);

        Assert.Equal("invalid", moved.State);
        Assert.Equal([(ImportCodes.InvalidValue, "income_phases[2].source")], ErrorsOf(moved));
    }

    [Fact]
    public async Task Replace_can_end_and_reopen_a_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, IncomeFile());
        var salary = await SourceIdAsync(app, "Salaire");
        static Dictionary<string, string> Ended(string endedOn) => IncomeFile(List(
            Salary.Replace("\"currency\":\"EUR\",", $"\"currency\":\"EUR\",\"ended_on\":\"{endedOn}\","),
            Cdd.Replace("\"ended_on\":\"2025-06-30\",", "")));

        // As close: a phase that starts on or after the end refuses it.
        var early = await ValidateAsync(client, app, app.AdultA, Ended("2025-08-31"), replace: true);

        Assert.Equal("invalid", early.State);
        Assert.Equal([(IncomeCodes.InvalidSource, "income_sources[0].ended_on")], ErrorsOf(early));
        await client.CancelRawAsync(app.AdultA, early.Id);

        var future = await ValidateAsync(client, app, app.AdultA, Ended("2999-01-01"), replace: true);

        Assert.Equal([(CoreFactCodes.DateInFuture, "income_sources[0].ended_on")], ErrorsOf(future));
        await client.CancelRawAsync(app.AdultA, future.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, Ended("2025-09-30"), replace: true);

        Assert.Equal(new CountsResponse(0, 0, 0, 2), replaced.Report!.Sections["income_sources"]);
        Assert.Equal(new CountsResponse(0, 4, 0), replaced.Report.Sections["income_phases"]);
        var open = await SourcesAsync(app, app.AdultA, archived: false);
        Assert.Equal(["CDD"], open.Select(s => s.GetProperty("displayName").GetString()));
        Assert.Equal(JsonValueKind.Null, open[0].GetProperty("endedOn").ValueKind);
        var ended = (await SourcesAsync(app, app.AdultA, archived: true)).Single(s => s.GetProperty("displayName").GetString() == "Salaire");
        Assert.True(ended.GetProperty("ended").GetBoolean());
        Assert.Equal("2025-09-30", ended.GetProperty("endedOn").GetString());
        // The running phases are cut on the end; the one over before it is left as it was.
        var phases = await StoredPhasesAsync(app, salary);
        Assert.Equal(new LocalDate(2025, 9, 1), phases.Single(p => p.Ref == "ip-1").EndsOn);
        Assert.Equal(new LocalDate(2025, 9, 30), phases.Single(p => p.Ref == "ip-2").EndsOn);
        Assert.Equal(new LocalDate(2025, 9, 30), phases.Single(p => p.Ref == "ip-3").EndsOn);

        var again = await CommitAsync(client, app, app.AdultA, Ended("2025-09-30"));

        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["income_sources"]);
        Assert.Equal(new CountsResponse(0, 4, 0), again.Report.Sections["income_phases"]);
    }

    [Fact]
    public async Task A_phase_added_after_the_end_of_a_source_that_stays_ended_is_reported_at_that_phase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, IncomeFile(List(Cdd), List(IpCdd), flows: false));
        var late = IpCdd.Replace("ip-cdd", "ip-late").Replace("\"label\":\"Salaire de base\"", "\"label\":\"Prime\"")
            .Replace("\"starts_on\":\"2025-01-01\",\"ends_on\":\"2025-06-30\"", "\"starts_on\":\"2025-07-15\"");

        var refused = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(Cdd), List(IpCdd, late), flows: false));

        Assert.Equal([(IncomeCodes.InvalidSource, "income_phases[1].starts_on")], ErrorsOf(refused));
        await client.CancelRawAsync(app.AdultA, refused.Id);
    }

    [Fact]
    public async Task A_new_ended_source_has_its_running_phases_cut_as_close_does()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var openEnded = IpCdd.Replace(",\"ends_on\":\"2025-06-30\"", "");
        var late = IpCdd.Replace("ip-cdd", "ip-late").Replace("\"label\":\"Salaire de base\"", "\"label\":\"Prime\"")
            .Replace("\"starts_on\":\"2025-01-01\",\"ends_on\":\"2025-06-30\"", "\"starts_on\":\"2025-06-30\"");

        var refused = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(Cdd), List(openEnded, late), flows: false));

        Assert.Equal([(IncomeCodes.InvalidSource, "income_sources[0].ended_on")], ErrorsOf(refused));
        Assert.Equal(new CountsResponse(0, 0, 0), refused.Report!.Sections["income_sources"]);
        Assert.Equal(new CountsResponse(0, 0, 0), refused.Report.Sections["income_phases"]);
        await client.CancelRawAsync(app.AdultA, refused.Id);

        var committed = await CommitAsync(client, app, app.AdultA, IncomeFile(List(Cdd), List(openEnded), flows: false));

        Assert.Equal(new CountsResponse(1, 0, 0), committed.Report!.Sections["income_phases"]);
        var phase = Assert.Single(await StoredPhasesAsync(app, await SourceIdAsync(app, "CDD")));
        Assert.Equal(("ip-cdd", new LocalDate(2025, 6, 30)), (phase.Ref, phase.EndsOn));
        var again = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(Cdd), List(openEnded), flows: false));
        Assert.Equal(new CountsResponse(0, 1, 0), again.Report!.Sections["income_phases"]);
        Assert.Empty(again.Report.Issues);
    }

    [Fact]
    public async Task Replace_follows_the_1F3b_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, IncomeFile());

        // Adult-1 drops adult-2 from the CDD's owners.
        var dropped = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(Salary, Cdd.Replace(
            """{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}""", """{"person":"adult-1","share_bp":10000}"""))), replace: true);
        Assert.Equal([(ImportCodes.NotPermitted, "income_sources[1].ownership")], ErrorsOf(dropped));
        await client.CancelRawAsync(app.AdultA, dropped.Id);

        // Adult-1, the salary's only owner, makes it private: adult-2 reads it today.
        var hidden = await ValidateAsync(client, app, app.AdultA,
            IncomeFile(List(Salary.Replace("\"visibility\":\"shared\"", "\"visibility\":\"private\""), Cdd)), replace: true);
        Assert.Equal([(ImportCodes.NotPermitted, "income_sources[0].visibility")], ErrorsOf(hidden));
        await client.CancelRawAsync(app.AdultA, hidden.Id);

        // A replacement keeps the currency.
        var usd = await ValidateAsync(client, app, app.AdultA,
            IncomeFile(List(Salary.Replace("\"currency\":\"EUR\"", "\"currency\":\"USD\""), Cdd)), replace: true);
        Assert.Equal([(CoreFactCodes.CurrencyMismatch, "income_sources[0].currency")], ErrorsOf(usd));
        await client.CancelRawAsync(app.AdultA, usd.Id);

        // Adult-2 reads the salary but cannot write it; its phases and its one-off are not reported again.
        var notWriter = await ValidateAsync(client, app, app.AdultB,
            IncomeFile(List(Salary.Replace("\"name\":\"Salaire\"", "\"name\":\"Salaire 2\""), Cdd)), replace: true);
        Assert.Equal([(ImportCodes.NotPermitted, "income_sources[0]")], ErrorsOf(notWriter));
        await client.CancelRawAsync(app.AdultB, notWriter.Id);

        // Nor may adult-2 add a phase to it.
        var added = await ValidateAsync(client, app, app.AdultB, IncomeFile(phases: List(Ip1, Ip2, Ip3, IpCdd, """
            {"id":"ip-b","source":"inc-salary-1","label":"Prime","starts_on":"2025-01-01",
             "net_amount":{"amount":"100","currency":"EUR"},"every":{"count":1,"unit":"year"}}
            """)));
        Assert.Equal([(ImportCodes.NotPermitted, "income_phases[4]")], ErrorsOf(added));
    }

    [Fact]
    public async Task Every_rule_is_reported_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        static string Phase(string id, string extra = "", string label = "P", string every = """{"count":1,"unit":"month"}""",
            string amount = "100", string currency = "EUR", string source = "inc-salary-1", string startsOn = "2025-01-01")
            => $$"""{"id":"{{id}}","source":"{{source}}","label":"{{label}}","starts_on":"{{startsOn}}","net_amount":{"amount":"{{amount}}","currency":"{{currency}}"},"every":{{every}}{{extra}}}""";

        // Entity-local rules: the receipt of the batch reports them.
        var session = await client.StartAsync(app.AdultA, Adults(app));
        var sources = await client.PutAsync(app.AdultA, session.Id, "income_sources", 0, List(
            Salary.Replace("\"kind\":\"salary\"", "\"kind\":\"lottery\""),
            Salary.Replace("\"name\":\"Salaire\"", "\"name\":\"  \"").Replace("inc-salary-1", "s1"),
            Salary.Replace("\"payer_label\":\"Employeur\"", $"\"payer_label\":\"{new string('p', 101)}\"").Replace("inc-salary-1", "s2"),
            Salary.Replace("\"currency\":\"EUR\",", "\"currency\":\"EUR\",\"ended_on\":\"2025-02-30\",").Replace("inc-salary-1", "s3")));
        var phases = await client.PutAsync(app.AdultA, session.Id, "income_phases", 0, List(
            Phase("p0", every: """{"count":2,"unit":"year"}"""),
            Phase("p1", amount: "0"),
            Phase("p2", label: new string('l', 61)),
            Phase("p3", ",\"ends_on\":\"2024-12-01\""),
            Phase("p4", every: """{"count":1,"unit":"week"}"""),
            Phase("p5", ",\"max_cycles\":0"),
            Phase("p6", ",\"gross_amount\":{\"amount\":\"-1\",\"currency\":\"EUR\"}"),
            Phase("p7", ",\"gross_amount\":{\"amount\":\"200\",\"currency\":\"USD\"}")));
        await client.CancelRawAsync(app.AdultA, session.Id);

        Assert.False(sources.Accepted);
        Assert.Equal(
            [
                (IncomeCodes.UnknownKind, "income_sources[0].kind"),
                (IncomeCodes.InvalidSource, "income_sources[1].name"),
                (IncomeCodes.InvalidSource, "income_sources[2].payer_label"),
                (ImportCodes.InvalidValue, "income_sources[3].ended_on"),
            ],
            sources.Issues.Select(i => (i.Code, i.Path)));
        Assert.False(phases.Accepted);
        Assert.Equal(
            [
                (IncomeCodes.UnknownCadence, "income_phases[0].every"),
                (CoreFactCodes.AmountNotPositive, "income_phases[1].net_amount.amount"),
                (IncomeCodes.InvalidPhase, "income_phases[2].label"),
                (IncomeCodes.InvalidPhase, "income_phases[3].ends_on"),
                (IncomeCodes.UnknownCadence, "income_phases[4].every"),
                (IncomeCodes.InvalidPhase, "income_phases[5].max_cycles"),
                (CoreFactCodes.AmountNotPositive, "income_phases[6].gross_amount.amount"),
                (CoreFactCodes.CurrencyMismatch, "income_phases[7].gross_amount.currency"),
            ],
            phases.Issues.Select(i => (i.Code, i.Path)));

        // Rules that need the household or the other entities: the validation reports them.
        var validated = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(Salary), List(
            Phase("g0", startsOn: "2024-09-01", label: "Salaire de base", extra: ",\"ends_on\":\"2025-09-01\""),
            Phase("g1", startsOn: "2025-08-01", label: "Salaire de base"),
            Phase("g2", currency: "USD"),
            Phase("g3", source: "inc-nobody")), flows: false));

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            new[]
            {
                (CoreFactCodes.CurrencyMismatch, "income_phases[2].net_amount.currency"),
                (ImportCodes.UnresolvedReference, "income_phases[3].source"),
                (IncomeCodes.PhaseOverlap, "income_phases[1].starts_on"),
            }.Order(),
            ErrorsOf(validated).Order());
        await client.CancelRawAsync(app.AdultA, validated.Id);

        var unowned = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(
            Salary.Replace("\"person\":\"adult-1\"", "\"person\":\"nobody\""),
            Cdd.Replace("\"share_bp\":5000}]", "\"share_bp\":4000}]"),
            Salary.Replace("inc-salary-1", "s-b").Replace("\"person\":\"adult-1\"", "\"person\":\"adult-2\"")), "[]", flows: false));

        Assert.Equal(
            [
                (ImportCodes.UnresolvedReference, "income_sources[0].ownership[0].person"),
                ("core.share_sum", "income_sources[1].ownership"),
                (ImportCodes.NotPermitted, "income_sources[2]"),
            ],
            ErrorsOf(unowned));
    }

    /// <summary>Review Focus #5: never a silent second running phase of one label.</summary>
    [Fact]
    public async Task A_file_phase_overlapping_a_phase_entered_by_hand_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, IncomeFile());
        var salary = await SourceIdAsync(app, "Salaire");
        // A raise entered by hand: ip-2 now ends on 2026-01-01, a phase of the API's starts there.
        var raise = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary}/phase-changes",
            new { label = "Salaire de base", effectiveOn = "2026-01-01", netAmount = new { amount = "2500", currency = "EUR" } });
        Assert.True(raise.IsSuccessStatusCode, await raise.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        static string Ip4(string startsOn) => $$$"""
            {"id":"ip-4","source":"inc-salary-1","label":"Salaire de base","starts_on":"{{{startsOn}}}",
             "net_amount":{"amount":"2600.00","currency":"EUR"},"every":{"count":1,"unit":"month"}}
            """;

        var overlapping = await ValidateAsync(client, app, app.AdultA, IncomeFile(phases: List(Ip1, Ip2, Ip3, Ip4("2025-12-01"), IpCdd)));

        Assert.Equal("invalid", overlapping.State);
        Assert.Equal([(IncomeCodes.PhaseOverlap, "income_phases[3].starts_on")], ErrorsOf(overlapping));
        await client.CancelRawAsync(app.AdultA, overlapping.Id);

        // After ip-2 as stored (it is unchanged in the file), within the phase entered by hand only.
        var afterIp2 = await ValidateAsync(client, app, app.AdultA, IncomeFile(phases: List(Ip1, Ip2, Ip3, Ip4("2026-03-01"), IpCdd)));

        Assert.Equal([(IncomeCodes.PhaseOverlap, "income_phases[3].starts_on")], ErrorsOf(afterIp2));
        await client.CancelRawAsync(app.AdultA, afterIp2.Id);

        // ip-2 unchanged counts as stored, not as the file (open-ended) has it: the file as committed still validates.
        var asCommitted = await ValidateAsync(client, app, app.AdultA, IncomeFile());
        Assert.Equal("validated", asCommitted.State);
        Assert.Equal(new CountsResponse(0, 4, 0), asCommitted.Report!.Sections["income_phases"]);
    }

    [Fact]
    public async Task Warnings_do_not_block()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var empty = Salary.Replace("inc-salary-1", "inc-empty").Replace("\"name\":\"Salaire\"", "\"name\":\"Vide\"");
        var endingSoon = Ip2.Replace("\"starts_on\":\"2025-09-01\",", "\"starts_on\":\"2025-09-01\",\"ends_on\":\"2025-11-30\",");

        var validated = await ValidateAsync(client, app, app.AdultA, IncomeFile(List(Salary, empty), List(Ip1, endingSoon, Ip3), flows: false));

        Assert.Equal("validated", validated.State);
        Assert.Equal(
            [
                ("warning", IncomeCodes.SourceWithoutPhase, "income_sources[1]"),
                ("warning", IncomeCodes.GapSoon, "income_phases[1]"),
            ],
            validated.Report!.Issues.Select(i => (i.Severity, i.Code, i.Path)));
        var committed = await client.CommitRawAsync(app.AdultA, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A source whose phases are stored has phases, though this file sends none.
        var sourcesOnly = await ValidateAsync(client, app, app.AdultA, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons, ["income_sources"] = List(Salary),
        });
        Assert.Equal("validated", sourcesOnly.State);
        Assert.Equal([(IncomeCodes.GapSoon, "income_sources[0]")], sourcesOnly.Report!.Issues.Select(i => (i.Code, i.Path)));
    }

    [Fact]
    public async Task An_id_of_another_adults_private_source_answers_as_a_new_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultB, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["income_sources"] = """
                [{"id":"inc-secret","name":"Secret","kind":"pension","currency":"EUR","visibility":"private","ownership":[{"person":"adult-2","share_bp":10000}]}]
                """,
            ["income_phases"] = """
                [{"id":"ip-secret","source":"inc-secret","label":"Pension","starts_on":"2025-01-01","net_amount":{"amount":"900","currency":"EUR"},"every":{"count":1,"unit":"month"}}]
                """,
        });

        static Dictionary<string, string> Mine(string source, string phase) => new()
        {
            ["persons"] = GoldenFile.Persons,
            ["income_sources"] = $$$"""
                [{"id":"{{{source}}}","name":"Mine","kind":"salary","currency":"USD","ownership":[{"person":"adult-1","share_bp":10000}]}]
                """,
            ["income_phases"] = $$$"""
                [{"id":"{{{phase}}}","source":"{{{source}}}","label":"Salaire","starts_on":"2025-02-01","net_amount":{"amount":"3000","currency":"USD"},"every":{"count":1,"unit":"month"}}]
                """,
        };
        var fresh = await ValidateAsync(client, app, app.AdultA, Mine("inc-fresh", "ip-fresh"));
        await client.CancelRawAsync(app.AdultA, fresh.Id);
        var taken = await ValidateAsync(client, app, app.AdultA, Mine("inc-secret", "ip-secret"));

        Assert.Equal("validated", taken.State);
        Assert.Empty(taken.Report!.Issues);
        Assert.Equal(new CountsResponse(1, 0, 0), taken.Report.Sections["income_sources"]);
        Assert.Equal(new CountsResponse(1, 0, 0), taken.Report.Sections["income_phases"]);
        Assert.Equal(JsonSerializer.Serialize(fresh.Report), JsonSerializer.Serialize(taken.Report));
        var committed = await client.CommitRawAsync(app.AdultA, taken.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["Mine"], (await SourcesAsync(app, app.AdultA, archived: true)).Select(s => s.GetProperty("displayName").GetString()));
        var mine = await SourceIdAsync(app, "Mine");
        Assert.Equal([3000m], (await StoredPhasesAsync(app, mine)).Select(p => p.Net));
        // Adult-2's source and phase are as they were.
        var secret = (await SourcesAsync(app, app.AdultB, archived: true)).Single(s => s.GetProperty("displayName").GetString() == "Secret");
        Assert.Equal([("ip-secret", 900m)], (await StoredPhasesAsync(app, secret.GetProperty("id").GetGuid())).Select(p => (p.Ref, p.Net)));
    }
}
