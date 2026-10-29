using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Export;
using Nid.TestSupport;
using NodaTime;
using static Nid.Host.IntegrationTests.Import.EnvelopesImportTests;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>2B1b spec §5: the Core's <c>valuations</c> and <c>flows</c> sections, asserted through the real API and the database.</summary>
public class FactsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public const string Valuations = """
        [{"id":"v-pea-1","item":"env-pea","as_of":"2024-12-31","amount":{"amount":"21000","currency":"EUR"}},
         {"id":"v-pea-2","item":"env-pea","as_of":"2025-09-30","amount":{"amount":25000.00,"currency":"EUR"}},
         {"id":"v-livret-0","item":"env-livret","as_of":"2025-07-31","amount":{"amount":"0","currency":"EUR"},"note":"clôture"}]
        """;

    public const string Flows = """
        [{"id":"f-pea-open","item":"env-pea","occurred_on":"2019-03-01","kind":"opening","amount":{"amount":"20000","currency":"EUR"}},
         {"id":"f-pea-in","item":"env-pea","occurred_on":"2025-03-15","kind":"contribution","amount":{"amount":"2000","currency":"EUR"}},
         {"id":"f-pea-fee","item":"env-pea","occurred_on":"2025-06-30","kind":"expense","amount":{"amount":0.1,"currency":"EUR"},"category":"fees"},
         {"id":"f-tr-out","item":"env-pea","occurred_on":"2025-07-01","kind":"withdrawal","amount":{"amount":"500","currency":"EUR"},"category":"transfer","external_ref":"transfer:t1"},
         {"id":"f-tr-in","item":"env-livret","occurred_on":"2025-07-01","kind":"contribution","amount":{"amount":"500","currency":"EUR"},"category":"transfer","external_ref":"transfer:t1"},
         {"id":"f-livret-out","item":"env-livret","occurred_on":"2025-07-31","kind":"withdrawal","amount":{"amount":"500","currency":"EUR"},"note":"clôture"}]
        """;

    public static Dictionary<string, string> Facts(string envelopes = EnvelopesImportTests.Envelopes, string valuations = Valuations, string flows = Flows)
        => new() { ["persons"] = GoldenFile.Persons, ["envelopes"] = envelopes, ["valuations"] = valuations, ["flows"] = flows };

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static decimal Amount(JsonElement figures, string name)
        => decimal.Parse(figures.GetProperty(name).GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> FiguresAsync(NidApp app, Guid envelope)
        => (await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{envelope}")).GetProperty("figures");

    private static List<(string Code, string Path)> ErrorsOf(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)).ToList();

    private static async Task PostAsync(NidApp app, string url, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Facts_are_written_as_imported_and_the_figures_follow()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, Facts());

        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report!.Sections["valuations"]);
        Assert.Equal(new CountsResponse(6, 0, 0), committed.Report.Sections["flows"]);
        var figures = await FiguresAsync(app, await IdOf(app, "PEA"));
        Assert.Equal(25000m, Amount(figures, "value"));
        Assert.Equal(21500m, Amount(figures, "netContributions"));
        Assert.Equal(0.1m, Amount(figures, "fees"));

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var values = await core.Valuations.ToListAsync();
        var flows = await core.Flows.ToListAsync();
        Assert.Equal(3, values.Count);
        Assert.Equal(6, flows.Count);
        Assert.All(values, v => Assert.Equal(("import", "import", app.AdultA.Value), (v.Source, v.CreatedVia, v.ImportedBy!.Value)));
        Assert.All(flows, f => Assert.Equal(("import", "import", app.AdultA.Value), (f.Source, f.CreatedVia, f.ImportedBy!.Value)));
        Assert.Equal(["v-livret-0", "v-pea-1", "v-pea-2"], values.Select(v => v.ImportRef!).Order());
        Assert.Equal(["f-livret-out", "f-pea-fee", "f-pea-in", "f-pea-open", "f-tr-in", "f-tr-out"], flows.Select(f => f.ImportRef!).Order());
        Assert.All(values, v => Assert.Matches("^[0-9a-f]{64}$", v.ImportHash!.Trim()));
    }

    /// <summary>Review Focus #1: the two legs of a transfer share one reference, each on its own item.</summary>
    [Fact]
    public async Task A_transfer_between_two_envelopes_of_the_file_keeps_its_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, Facts());

        Assert.DoesNotContain(committed.Report!.Issues, i => i.Code == ImportCodes.DuplicateFact);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var legs = await core.Flows.Where(f => f.ExternalRef == "transfer:t1" && f.RetiredAt == null).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.Equal(
            [(await IdOf(app, "Livret A"), "contribution"), (await IdOf(app, "PEA"), "withdrawal")],
            legs.Select(l => (l.ItemId, l.Kind)).OrderBy(l => l.Kind, StringComparer.Ordinal));
    }

    /// <summary>Review Focus #3 (and Task 4's deferred test 8): closed_on against the file's own facts.</summary>
    [Fact]
    public async Task A_closed_envelope_gets_its_history_and_stays_closed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        var late = await ValidateAsync(client, app, app.AdultA,
            Facts(valuations: Valuations.Replace("\"as_of\":\"2025-07-31\"", "\"as_of\":\"2025-08-01\"")));
        Assert.Equal("invalid", late.State);
        Assert.Equal([("investments.invalid_envelope", "envelopes[1].closed_on")], ErrorsOf(late));
        await client.CancelRawAsync(app.AdultA, late.Id);

        var committed = await CommitAsync(client, app, app.AdultA, Facts());

        Assert.Equal("committed", committed.State);
        Assert.DoesNotContain(await ListAsync(app, app.AdultA, archived: false), e => e.GetProperty("displayName").GetString() == "Livret A");
        var livret = await IdOf(app, "Livret A");
        var detail = await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{livret}");
        Assert.True(detail.GetProperty("closed").GetBoolean());
        Assert.Equal(0m, Amount(detail.GetProperty("figures"), "value"));
        Assert.Single(detail.GetProperty("valuations").EnumerateArray());
        Assert.Equal(2, detail.GetProperty("flows").GetArrayLength());
    }

    /// <summary>Review Focus #2: a reimport touches nothing, and a value entered by hand since stays current.</summary>
    [Fact]
    public async Task The_same_file_again_is_unchanged_and_a_value_entered_by_hand_stays()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Facts());
        var pea = await IdOf(app, "PEA");
        // The brief's 2025-10-31 is after the test host's today (2025-10-15): the latest day it takes.
        await PostAsync(app, $"/api/v1/investments/envelopes/{pea}/valuations", new { asOf = "2025-10-10", amount = Eur("26000.00") });

        var again = await CommitAsync(client, app, app.AdultA, Facts());

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["valuations"]);
        Assert.Equal(new CountsResponse(0, 6, 0), again.Report.Sections["flows"]);
        Assert.Equal(26000m, Amount(await FiguresAsync(app, pea), "value"));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var hand = await core.Valuations.SingleAsync(v => v.ItemId == pea && v.AsOf == new LocalDate(2025, 10, 10));
        Assert.Null(hand.RetiredAt);
        Assert.Equal(3, await core.Valuations.CountAsync(v => v.RetiredAt == null && v.ImportRef != null));

        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);
        var exported = Assert.Single(export["valuations"], v => v.GetProperty("as_of").GetString() == "2025-10-10");
        Assert.Equal($"val-{hand.Id:D}", exported.GetProperty("id").GetString());
        Assert.Equal("env-pea", exported.GetProperty("item").GetString());
    }

    [Fact]
    public async Task Other_content_is_a_conflict_and_replace_corrects_through_the_chain()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Facts());
        var pea = await IdOf(app, "PEA");
        Guid old;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            old = (await core.Valuations.SingleAsync(v => v.ImportRef == "v-pea-2")).Id;
        var changed = Facts(valuations: Valuations.Replace("25000.00", "26000"));

        var conflict = await ValidateAsync(client, app, app.AdultA, changed);

        Assert.Equal("invalid", conflict.State);
        Assert.Equal([(ImportCodes.Conflict, "valuations[1]")], ErrorsOf(conflict));
        Assert.Equal(new CountsResponse(0, 2, 1), conflict.Report!.Sections["valuations"]);
        await client.CancelRawAsync(app.AdultA, conflict.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 2, 0, 1), replaced.Report!.Sections["valuations"]);
        Assert.Equal(new CountsResponse(0, 6, 0), replaced.Report.Sections["flows"]);
        Assert.Equal(26000m, Amount(await FiguresAsync(app, pea), "value"));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var current = await core.Valuations.SingleAsync(v => v.ImportRef == "v-pea-2" && v.RetiredAt == null);
            Assert.Equal((26000m, old), (current.Amount, current.SupersedesId!.Value));
            Assert.NotNull((await core.Valuations.SingleAsync(v => v.Id == old)).RetiredAt);
        }
    }

    /// <summary>A correction that moves a value frees its date for a new value of the same file, whatever their order.</summary>
    [Fact]
    public async Task A_correction_frees_its_date_for_a_new_value_of_the_file()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Facts());
        const string moved = """
            [{"id":"v-pea-1","item":"env-pea","as_of":"2024-12-31","amount":{"amount":"21000","currency":"EUR"}},
             {"id":"v-pea-3","item":"env-pea","as_of":"2025-09-30","amount":{"amount":"25500","currency":"EUR"}},
             {"id":"v-pea-2","item":"env-pea","as_of":"2025-09-29","amount":{"amount":25000.00,"currency":"EUR"}},
             {"id":"v-livret-0","item":"env-livret","as_of":"2025-07-31","amount":{"amount":"0","currency":"EUR"},"note":"clôture"}]
            """;

        var replaced = await CommitAsync(client, app, app.AdultA, Facts(valuations: moved), replace: true);

        Assert.Equal(new CountsResponse(1, 2, 0, 1), replaced.Report!.Sections["valuations"]);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var pea = await IdOf(app, "PEA");
        var values = await core.Valuations.Where(v => v.RetiredAt == null && v.ItemId == pea).OrderBy(v => v.AsOf).ToListAsync();
        Assert.Equal([("v-pea-1", 21000m), ("v-pea-2", 25000m), ("v-pea-3", 25500m)], values.Select(v => (v.ImportRef!, v.Amount)));
        Assert.Equal(new LocalDate(2025, 9, 29), values[1].AsOf);
    }

    [Fact]
    public async Task A_collision_with_a_fact_entered_by_hand_is_a_conflict_and_replace_corrects_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        using var fullValues = JsonDocument.Parse(Valuations);
        using var fullFlows = JsonDocument.Parse(Flows);
        string Without(JsonDocument all, params string[] ids)
            => "[" + string.Join(",", all.RootElement.EnumerateArray().Where(e => !ids.Contains(e.GetProperty("id").GetString())).Select(e => e.GetRawText())) + "]";
        await CommitAsync(client, app, app.AdultA, Facts(valuations: Without(fullValues, "v-pea-2"), flows: Without(fullFlows, "f-pea-open", "f-tr-out")));
        var pea = await IdOf(app, "PEA");
        var url = $"/api/v1/investments/envelopes/{pea}";
        await PostAsync(app, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("24000.00") });
        await PostAsync(app, $"{url}/flows", new { occurredOn = "2019-03-01", kind = "opening", amount = Eur("19000.00") });
        await PostAsync(app, $"{url}/flows", new { occurredOn = "2025-07-02", kind = "withdrawal", amount = Eur("400.00"), externalRef = "transfer:t1" });
        Dictionary<string, Guid> hand;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            hand = new Dictionary<string, Guid>
            {
                ["value"] = (await core.Valuations.SingleAsync(v => v.ImportRef == null)).Id,
                ["opening"] = (await core.Flows.SingleAsync(f => f.ImportRef == null && f.Kind == "opening")).Id,
                ["ref"] = (await core.Flows.SingleAsync(f => f.ImportRef == null && f.ExternalRef == "transfer:t1")).Id,
            };
        }

        var conflict = await ValidateAsync(client, app, app.AdultA, Facts());

        Assert.Equal("invalid", conflict.State);
        Assert.Equal([(ImportCodes.Conflict, "valuations[1]"), (ImportCodes.Conflict, "flows[0]"), (ImportCodes.Conflict, "flows[3]")], ErrorsOf(conflict));
        Assert.Equal(new CountsResponse(0, 2, 1), conflict.Report!.Sections["valuations"]);
        Assert.Equal(new CountsResponse(0, 4, 2), conflict.Report.Sections["flows"]);
        await client.CancelRawAsync(app.AdultA, conflict.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, Facts(), replace: true);

        Assert.Equal(new CountsResponse(0, 2, 0, 1), replaced.Report!.Sections["valuations"]);
        Assert.Equal(new CountsResponse(0, 4, 0, 2), replaced.Report.Sections["flows"]);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            Assert.All(await core.Valuations.Where(v => v.Id == hand["value"]).ToListAsync(), v => Assert.NotNull(v.RetiredAt));
            Assert.All(await core.Flows.Where(f => f.Id == hand["opening"] || f.Id == hand["ref"]).ToListAsync(), f => Assert.NotNull(f.RetiredAt));
            var value = await core.Valuations.SingleAsync(v => v.ImportRef == "v-pea-2");
            Assert.Equal((hand["value"], 25000m, "import"), (value.SupersedesId!.Value, value.Amount, value.Source));
            var opening = await core.Flows.SingleAsync(f => f.ImportRef == "f-pea-open");
            Assert.Equal((hand["opening"], 20000m), (opening.SupersedesId!.Value, opening.Amount));
            var leg = await core.Flows.SingleAsync(f => f.ImportRef == "f-tr-out");
            Assert.Equal((hand["ref"], new LocalDate(2025, 7, 1)), (leg.SupersedesId!.Value, leg.OccurredOn));
        }
        Assert.Equal(25000m, Amount(await FiguresAsync(app, pea), "value"));
    }

    [Fact]
    public async Task Duplicates_in_the_file()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string values = """
            [{"id":"v-1","item":"env-pea","as_of":"2024-12-31","amount":{"amount":"21000","currency":"EUR"}},
             {"id":"v-2","item":"env-pea","as_of":"2024-12-31","amount":{"amount":"21500","currency":"EUR"}},
             {"id":"v-3","item":"env-livret","as_of":"2024-12-31","amount":{"amount":"100","currency":"EUR"}}]
            """;
        const string flows = """
            [{"id":"f-1","item":"env-pea","occurred_on":"2019-03-01","kind":"opening","amount":{"amount":"20000","currency":"EUR"}},
             {"id":"f-2","item":"env-pea","occurred_on":"2019-04-01","kind":"opening","amount":{"amount":"20000","currency":"EUR"}},
             {"id":"f-3","item":"env-pea","occurred_on":"2025-01-01","kind":"contribution","amount":{"amount":"1","currency":"EUR"},"external_ref":"x"},
             {"id":"f-4","item":"env-pea","occurred_on":"2025-01-02","kind":"withdrawal","amount":{"amount":"1","currency":"EUR"},"external_ref":"x"},
             {"id":"f-5","item":"env-livret","occurred_on":"2025-01-02","kind":"opening","amount":{"amount":"1","currency":"EUR"},"external_ref":"x"}]
            """;

        var validated = await ValidateAsync(client, app, app.AdultA, Facts(valuations: values, flows: flows));

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            [(ImportCodes.DuplicateFact, "valuations[1]"), (ImportCodes.DuplicateFact, "flows[1]"), (ImportCodes.DuplicateFact, "flows[3]")],
            ErrorsOf(validated));
    }

    /// <summary>Review Focus #4 and #5.</summary>
    [Fact]
    public async Task Every_rule_is_reported_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        // Entity-local rules: the receipt of the batch reports them.
        var session = await client.StartAsync(app.AdultA, Adults(app));
        var localValues = await client.PutAsync(app.AdultA, session.Id, "valuations", 0, $$$"""
            [{"id":"v-0","item":"env-pea","as_of":"2025-02-30","amount":{"amount":"1","currency":"EUR"}},
             {"id":"v-1","item":"env-pea","as_of":"2025-02-01","amount":{"amount":"1","currency":"EURO"}},
             {"id":"v-2","item":"env-pea","as_of":"2025-02-01","amount":{"amount":"1.00001","currency":"EUR"}},
             {"id":"v-3","item":"env-pea","as_of":"2025-02-01","amount":{"amount":"1","currency":"EUR"},"note":"{{{new string('n', 2001)}}}"},
             {"id":"V 4","item":"env-pea","as_of":"2025-02-01","amount":{"amount":"1","currency":"EUR"}},
             {"id":"v-5","item":"env-pea","as_of":"2025-02-01","amount":{"amount":"1","currency":"EUR"},"colour":"blue"}]
            """);
        var localFlows = await client.PutAsync(app.AdultA, session.Id, "flows", 0, """
            [{"id":"f-0","item":"env-pea","occurred_on":"2025-02-01","kind":"gift","amount":{"amount":"1","currency":"EUR"}},
             {"id":"f-1","item":"env-pea","occurred_on":"2025-02-01","kind":"contribution","amount":{"amount":"0","currency":"EUR"}},
             {"id":"f-2","item":"env-pea","occurred_on":"2025-02-01","kind":"contribution","amount":{"amount":"-5","currency":"EUR"}},
             {"id":"f-3","item":"env-pea","occurred_on":"2025-02-01","kind":"expense","amount":{"amount":"1","currency":"EUR"},"category":"Frais"},
             {"id":"f-4","item":"env-pea","occurred_on":"2025-02-01","kind":"expense","amount":{"amount":"1","currency":"EUR"},"external_ref":" x"}]
            """);
        await client.CancelRawAsync(app.AdultA, session.Id);

        Assert.False(localValues.Accepted);
        Assert.Equal(
            [
                (ImportCodes.InvalidValue, "valuations[0].as_of"),
                (ImportCodes.InvalidValue, "valuations[1].amount.currency"),
                ("core.money_scale", "valuations[2].amount.amount"),
                (CoreFactCodes.InvalidFact, "valuations[3].note"),
                (ImportCodes.InvalidId, "valuations[4].id"),
                (ImportCodes.MalformedEntity, "valuations[5].colour"),
            ],
            localValues.Issues.Select(i => (i.Code, i.Path)));
        Assert.False(localFlows.Accepted);
        Assert.Equal(
            [
                (ImportCodes.InvalidValue, "flows[0].kind"),
                (CoreFactCodes.AmountNotPositive, "flows[1].amount.amount"),
                (CoreFactCodes.AmountNotPositive, "flows[2].amount.amount"),
                (CoreFactCodes.InvalidFact, "flows[3].category"),
                (CoreFactCodes.InvalidFact, "flows[4].external_ref"),
            ],
            localFlows.Issues.Select(i => (i.Code, i.Path)));

        // Rules that need the other sections and the household: the validation reports them.
        var envelopes = EnvelopesImportTests.Envelopes.TrimEnd().TrimEnd(']') + """
            ,{"id":"env-cc","name":"Compte courant","kind":"checking","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]}]
            """;
        var file = GoldenFile.Sections();
        file["envelopes"] = envelopes;
        file["valuations"] = """
            [{"id":"v-0","item":"nowhere","as_of":"2025-01-31","amount":{"amount":"1","currency":"EUR"}},
             {"id":"v-1","item":"sub-spotify","as_of":"2025-01-31","amount":{"amount":"1","currency":"EUR"}},
             {"id":"v-2","item":"env-pea","as_of":"2025-01-31","amount":{"amount":"1","currency":"USD"}},
             {"id":"v-3","item":"env-pea","as_of":"2999-01-01","amount":{"amount":"1","currency":"EUR"}},
             {"id":"v-4","item":"env-cc","as_of":"2025-01-31","amount":{"amount":"-150.25","currency":"EUR"}},
             {"id":"v-5","item":"env-pea","as_of":"2025-02-28","amount":{"amount":21000,"currency":"EUR"}}]
            """;
        file["flows"] = """
            [{"id":"f-0","item":"env-pea","occurred_on":"2999-01-01","kind":"contribution","amount":{"amount":"1","currency":"EUR"}},
             {"id":"f-1","item":"env-pea","occurred_on":"2025-01-01","kind":"contribution","amount":{"amount":"1","currency":"GBP"}}]
            """;

        var validated = await ValidateAsync(client, app, app.AdultA, file);

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            [
                (ImportCodes.UnresolvedReference, "valuations[0].item"),
                (CoreFactCodes.ItemWithoutCurrency, "valuations[1].item"),
                (CoreFactCodes.CurrencyMismatch, "valuations[2].amount.currency"),
                (CoreFactCodes.DateInFuture, "valuations[3].as_of"),
                (CoreFactCodes.DateInFuture, "flows[0].occurred_on"),
                (CoreFactCodes.CurrencyMismatch, "flows[1].amount.currency"),
            ],
            ErrorsOf(validated));
        Assert.Equal(new CountsResponse(2, 0, 0), validated.Report!.Sections["valuations"]);
        await client.CancelRawAsync(app.AdultA, validated.Id);

        // The accepted ones, read back exactly: a negative value on a checking account, a JSON number.
        file["valuations"] = """
            [{"id":"v-4","item":"env-cc","as_of":"2025-01-31","amount":{"amount":"-150.25","currency":"EUR"}},
             {"id":"v-5","item":"env-pea","as_of":"2025-02-28","amount":{"amount":21000,"currency":"EUR"}}]
            """;
        file.Remove("flows");
        await CommitAsync(client, app, app.AdultA, file);

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(-150.25m, (await core.Valuations.SingleAsync(v => v.ImportRef == "v-4")).Amount);
        Assert.Equal(21000m, (await core.Valuations.SingleAsync(v => v.ImportRef == "v-5")).Amount);
        var export = await new ExportClient(app).ExportAllAsync(app.AdultA);
        var negative = export["valuations"].Single(v => v.GetProperty("id").GetString() == "v-4").GetProperty("amount");
        Assert.Equal(-150.25m, decimal.Parse(negative.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// An envelope refused by the validation takes its facts with it. Deviation: the brief's unknown kind is refused
    /// on receipt, so the batch is never staged; a share sum off is refused by the validation instead.
    /// </summary>
    [Fact]
    public async Task Facts_of_a_refused_envelope_are_not_reported_again()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string envelopes = """
            [{"id":"env-ok","name":"OK","kind":"savings","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]},
             {"id":"env-bad","name":"Bad","kind":"savings","currency":"EUR","ownership":[{"person":"adult-1","share_bp":9000}]}]
            """;
        const string values = """
            [{"id":"v-1","item":"env-bad","as_of":"2025-01-31","amount":{"amount":"1","currency":"USD"}},
             {"id":"v-2","item":"env-bad","as_of":"2999-01-31","amount":{"amount":"1","currency":"EUR"}},
             {"id":"v-3","item":"env-ok","as_of":"2025-01-31","amount":{"amount":"1","currency":"EUR"}}]
            """;

        var validated = await ValidateAsync(client, app, app.AdultA, Facts(envelopes, values, "[]"));

        Assert.Equal("invalid", validated.State);
        Assert.Equal([("core.share_sum", "envelopes[1].ownership")], ErrorsOf(validated));
        Assert.Equal(new CountsResponse(1, 0, 0), validated.Report!.Sections["valuations"]);
    }

    /// <summary>A shared envelope is readable, not writable, by an adult who does not own it: its facts are unchanged, a new one refused.</summary>
    [Fact]
    public async Task A_new_fact_on_an_envelope_the_importer_cannot_change_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string envelopes = """
            [{"id":"env-b","name":"B's","kind":"savings","currency":"EUR","ownership":[{"person":"adult-2","share_bp":10000}]}]
            """;
        const string values = """
            [{"id":"v-b-1","item":"env-b","as_of":"2025-01-31","amount":{"amount":"100","currency":"EUR"}}]
            """;
        await CommitAsync(client, app, app.AdultB, Facts(envelopes, values, "[]"));

        var added = values.TrimEnd().TrimEnd(']') + """
            ,{"id":"v-b-2","item":"env-b","as_of":"2025-02-28","amount":{"amount":"110","currency":"EUR"}}]
            """;
        var validated = await ValidateAsync(client, app, app.AdultA, Facts(envelopes, added, "[]"));

        Assert.Equal("invalid", validated.State);
        Assert.Equal([(ImportCodes.NotPermitted, "valuations[1]")], ErrorsOf(validated));
        Assert.Equal(new CountsResponse(0, 1, 0), validated.Report!.Sections["envelopes"]);
        Assert.Equal(new CountsResponse(0, 1, 0), validated.Report.Sections["valuations"]);
    }

    /// <summary>Privacy: as Task 4's test 9, for a value.</summary>
    [Fact]
    public async Task An_id_of_another_adults_private_value_answers_as_a_new_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultB, Facts("""
            [{"id":"env-secret","name":"Secret","kind":"pea","currency":"EUR","visibility":"private","ownership":[{"person":"adult-2","share_bp":10000}]}]
            """, """
            [{"id":"v-secret","item":"env-secret","as_of":"2025-01-31","amount":{"amount":"999","currency":"EUR"}}]
            """, """
            [{"id":"f-secret","item":"env-secret","occurred_on":"2025-01-01","kind":"opening","amount":{"amount":"900","currency":"EUR"},"external_ref":"r-secret"}]
            """));

        const string mine = """
            [{"id":"env-mine","name":"Mine","kind":"savings","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]}]
            """;
        static string Value(string id) => $$$"""
            [{"id":"{{{id}}}","item":"env-mine","as_of":"2025-01-31","amount":{"amount":"5","currency":"EUR"}}]
            """;
        static string Flow(string id) => $$$"""
            [{"id":"{{{id}}}","item":"env-mine","occurred_on":"2025-01-01","kind":"opening","amount":{"amount":"5","currency":"EUR"},"external_ref":"r-secret"}]
            """;
        var fresh = await ValidateAsync(client, app, app.AdultA, Facts(mine, Value("v-fresh"), Flow("f-fresh")));
        await client.CancelRawAsync(app.AdultA, fresh.Id);
        var taken = await ValidateAsync(client, app, app.AdultA, Facts(mine, Value("v-secret"), Flow("f-secret")));

        Assert.Equal("validated", taken.State);
        Assert.Empty(taken.Report!.Issues);
        Assert.Equal(new CountsResponse(1, 0, 0), taken.Report.Sections["valuations"]);
        Assert.Equal(JsonSerializer.Serialize(fresh.Report), JsonSerializer.Serialize(taken.Report));
        var committed = await client.CommitRawAsync(app.AdultA, taken.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(2, await core.Valuations.CountAsync(v => v.ImportRef == "v-secret" && v.RetiredAt == null));
        Assert.Equal(999m, (await core.Valuations.SingleAsync(v => v.ImportRef == "v-secret" && v.ImportedBy == app.AdultB.Value)).Amount);
    }

    [Fact]
    public async Task A_replaced_value_stays_on_its_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Facts());

        var moved = await ValidateAsync(client, app, app.AdultA,
            Facts(valuations: Valuations.Replace("\"id\":\"v-pea-1\",\"item\":\"env-pea\"", "\"id\":\"v-pea-1\",\"item\":\"env-livret\"")), replace: true);

        Assert.Equal("invalid", moved.State);
        Assert.Equal([(ImportCodes.InvalidValue, "valuations[0].item")], ErrorsOf(moved));
    }

    /// <summary>Not reachable through the API (an envelope's import ref never changes): pinned by moving a row in SQL.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stored_fact_found_on_another_item_is_refused_whatever_its_hash(bool replace)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Facts());
        var livret = await IdOf(app, "Livret A");
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Valuations.Where(v => v.ImportRef == "v-pea-2").ExecuteUpdateAsync(u => u.SetProperty(v => v.ItemId, livret));

        var again = await ValidateAsync(client, app, app.AdultA, Facts(), replace: replace);

        Assert.Equal("invalid", again.State);
        Assert.Equal([(ImportCodes.InvalidValue, "valuations[1].item")], ErrorsOf(again));
    }

    [Fact]
    public async Task A_correction_onto_the_date_of_another_current_fact_is_refused_even_with_replace()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, Facts());
        var pea = await IdOf(app, "PEA");
        await PostAsync(app, $"/api/v1/investments/envelopes/{pea}/valuations", new { asOf = "2025-10-10", amount = Eur("26000.00") });

        var onto = await ValidateAsync(client, app, app.AdultA,
            Facts(valuations: Valuations.Replace("\"as_of\":\"2025-09-30\"", "\"as_of\":\"2025-10-10\"")), replace: true);

        Assert.Equal("invalid", onto.State);
        Assert.Equal([(ImportCodes.Conflict, "valuations[1]")], ErrorsOf(onto));
    }
}
