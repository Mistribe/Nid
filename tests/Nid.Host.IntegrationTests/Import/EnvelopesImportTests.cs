using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>2B1b spec §4: the <c>envelopes</c> section, asserted through the real investments API.</summary>
public class EnvelopesImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public const string Envelopes = """
        [{"id":"env-pea","name":"PEA","kind":"pea","institution":"Banque","account_hint":"1234","opened_on":"2019-03-01",
          "currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}],"visibility":"private"},
         {"id":"env-livret","name":"Livret A","kind":"savings","currency":"EUR","closed_on":"2025-07-31",
          "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}]}]
        """;

    public static Dictionary<string, string> File(string envelopes = Envelopes) => new() { ["persons"] = GoldenFile.Persons, ["envelopes"] = envelopes };

    internal static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    internal static async Task<SessionResponse> StageAsync(ImportClient client, NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections)
    {
        var session = await client.StartAsync(actor, Adults(app));
        foreach (var (section, entities) in sections)
        {
            var batch = await client.PutAsync(actor, session.Id, section, 0, entities);
            Assert.True(batch.Accepted, string.Join("; ", batch.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        }
        return session;
    }

    internal static async Task<SessionResponse> ValidateAsync(
        ImportClient client, NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections, bool replace = false)
        => await client.ValidateAsync(actor, (await StageAsync(client, app, actor, sections)).Id, replace);

    internal static async Task<SessionResponse> CommitAsync(
        ImportClient client, NidApp app, PersonId actor, IReadOnlyDictionary<string, string> sections, bool replace = false)
    {
        var validated = await ValidateAsync(client, app, actor, sections, replace);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")));
        var committed = await client.CommitRawAsync(actor, validated.Id, replace);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(actor, validated.Id);
    }

    internal static async Task<JsonElement> JsonAsync(NidApp app, PersonId reader, string url)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    internal static async Task<List<JsonElement>> ListAsync(NidApp app, PersonId reader, bool archived)
        => (await JsonAsync(app, reader, "/api/v1/investments/envelopes?scope=me" + (archived ? "&archived=true" : "")))
            .GetProperty("items").EnumerateArray().ToList();

    internal static async Task<Guid> IdOf(NidApp app, string name)
        => (await ListAsync(app, app.AdultA, archived: true)).Single(e => e.GetProperty("displayName").GetString() == name).GetProperty("id").GetGuid();

    private static async Task<Envelope> StoredAsync(NidApp app, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IEnvelopeStore>().FindAsync(new ItemId(id), default))!;
    }

    internal static string Errors(SessionResponse session) => string.Join("; ", session.Report!.Issues.Select(i => $"{i.Code} at {i.Path}"));

    [Fact]
    public async Task Envelopes_are_created_with_their_owners_visibility_and_closing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var committed = await CommitAsync(new ImportClient(app), app, app.AdultA, File());

        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report!.Sections["envelopes"]);
        var pea = await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{await IdOf(app, "PEA")}");
        Assert.Equal("private", pea.GetProperty("visibility").GetString());
        Assert.Equal("Banque", pea.GetProperty("institution").GetString());
        Assert.Equal("1234", pea.GetProperty("accountHint").GetString());
        Assert.Equal("2019-03-01", pea.GetProperty("openedOn").GetString());
        Assert.False(pea.GetProperty("closed").GetBoolean());

        Assert.DoesNotContain(await ListAsync(app, app.AdultA, archived: false), e => e.GetProperty("displayName").GetString() == "Livret A");
        var livret = await IdOf(app, "Livret A");
        var detail = await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{livret}");
        Assert.True(detail.GetProperty("closed").GetBoolean());
        Assert.Equal("shared", detail.GetProperty("visibility").GetString());
        Assert.Equal(new LocalDate(2025, 7, 31), (await StoredAsync(app, livret)).ClosedOn);
        Assert.Null((await StoredAsync(app, await IdOf(app, "PEA"))).ClosedOn);
        // The co-owner reads the shared one, never the private one.
        var forB = await ListAsync(app, app.AdultB, archived: true);
        Assert.Equal(["Livret A"], forB.Select(e => e.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task The_same_file_again_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File());

        var again = await CommitAsync(client, app, app.AdultA, File());

        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["envelopes"]);
        Assert.Equal(2, (await ListAsync(app, app.AdultA, archived: true)).Count);
    }

    [Fact]
    public async Task Other_content_is_a_conflict_and_replace_updates_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File());
        var id = await IdOf(app, "PEA");
        var renamed = File(Envelopes.Replace("\"name\":\"PEA\"", "\"name\":\"PEA Bourse\""));

        var validated = await ValidateAsync(client, app, app.AdultA, renamed);

        Assert.Equal("invalid", validated.State);
        var conflict = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.Conflict, "envelopes[0]"), (conflict.Code, conflict.Path));
        Assert.Equal(new CountsResponse(0, 1, 1), validated.Report.Sections["envelopes"]);
        await client.CancelRawAsync(app.AdultA, validated.Id);

        var replaced = await CommitAsync(client, app, app.AdultA, renamed, replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 1), replaced.Report!.Sections["envelopes"]);
        var detail = await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{id}");
        Assert.Equal("PEA Bourse", detail.GetProperty("displayName").GetString());
        Assert.Equal(2, (await ListAsync(app, app.AdultA, archived: true)).Count);
    }

    [Fact]
    public async Task Replace_may_close_and_reopen()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File());

        var changed = Envelopes
            .Replace(",\"closed_on\":\"2025-07-31\"", "")
            .Replace("\"opened_on\":\"2019-03-01\",", "\"opened_on\":\"2019-03-01\",\"closed_on\":\"2025-08-31\",");
        var replaced = await CommitAsync(client, app, app.AdultA, File(changed), replace: true);

        Assert.Equal(new CountsResponse(0, 0, 0, 2), replaced.Report!.Sections["envelopes"]);
        var open = await ListAsync(app, app.AdultA, archived: false);
        Assert.Equal(["Livret A"], open.Select(e => e.GetProperty("displayName").GetString()));
        Assert.Null((await StoredAsync(app, await IdOf(app, "Livret A"))).ClosedOn);
        var pea = await IdOf(app, "PEA");
        Assert.Equal(new LocalDate(2025, 8, 31), (await StoredAsync(app, pea)).ClosedOn);
        Assert.True((await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{pea}")).GetProperty("closed").GetBoolean());
    }

    /// <summary>Controller ruling (fix round 1): a replacement closes no earlier than the envelope's stored facts, as <c>close</c>.</summary>
    [Fact]
    public async Task A_replacement_closes_after_the_stored_values_and_flows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File());
        var pea = await IdOf(app, "PEA");
        var entered = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{pea}/valuations",
            new { asOf = "2025-09-30", amount = new { amount = "1500.00", currency = "EUR" } });
        Assert.True(entered.IsSuccessStatusCode, await entered.Content.ReadAsStringAsync());
        static Dictionary<string, string> ClosedOn(string date)
            => File(Envelopes.Replace("\"opened_on\":\"2019-03-01\",", $"\"opened_on\":\"2019-03-01\",\"closed_on\":\"{date}\","));

        var early = await ValidateAsync(client, app, app.AdultA, ClosedOn("2025-09-15"), replace: true);

        Assert.Equal("invalid", early.State);
        var issue = Assert.Single(early.Report!.Issues, i => i.Severity == "error");
        Assert.Equal(("investments.invalid_envelope", "envelopes[0].closed_on"), (issue.Code, issue.Path));
        await client.CancelRawAsync(app.AdultA, early.Id);

        var sameDay = await CommitAsync(client, app, app.AdultA, ClosedOn("2025-09-30"), replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0, 1), sameDay.Report!.Sections["envelopes"]);
        Assert.Equal(new LocalDate(2025, 9, 30), (await StoredAsync(app, pea)).ClosedOn);
        Assert.True((await JsonAsync(app, app.AdultA, $"/api/v1/investments/envelopes/{pea}")).GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task Replace_does_not_change_the_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultA, File());

        var first = Envelopes.IndexOf("\"currency\":\"EUR\"", StringComparison.Ordinal); // the PEA's
        var usd = File(Envelopes[..first] + "\"currency\":\"USD\"" + Envelopes[(first + "\"currency\":\"EUR\"".Length)..]);
        var validated = await ValidateAsync(client, app, app.AdultA, usd, replace: true);

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((CoreFactCodes.CurrencyMismatch, "envelopes[0].currency"), (issue.Code, issue.Path));
    }

    [Fact]
    public async Task Replace_follows_the_1F3b_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string file = """
            [{"id":"env-cto","name":"CTO","kind":"brokerage","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]},
             {"id":"env-livret","name":"Livret A","kind":"savings","currency":"EUR",
              "ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}]}]
            """;
        await CommitAsync(client, app, app.AdultA, File(file));

        // Adult-1 drops adult-2 from the Livret's owners.
        var dropped = await ValidateAsync(client, app, app.AdultA, File(file.Replace(
            """{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}""", """{"person":"adult-1","share_bp":10000}""")), replace: true);
        var issue = Assert.Single(dropped.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "envelopes[1].ownership"), (issue.Code, issue.Path));
        await client.CancelRawAsync(app.AdultA, dropped.Id);

        // Adult-1, the CTO's only owner, makes it private: adult-2 reads it today.
        var hidden = await ValidateAsync(client, app, app.AdultA, File(file.Replace(
            "\"name\":\"CTO\",", "\"name\":\"CTO\",\"visibility\":\"private\",")), replace: true);
        issue = Assert.Single(hidden.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "envelopes[0].visibility"), (issue.Code, issue.Path));

        // Adult-2 reads the CTO but cannot write it.
        var notWriter = await ValidateAsync(client, app, app.AdultB, File(file.Replace("\"name\":\"CTO\"", "\"name\":\"CTO 2\"")), replace: true);
        issue = Assert.Single(notWriter.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "envelopes[0]"), (issue.Code, issue.Path));
    }

    [Fact]
    public async Task Every_rule_is_reported_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        static string Env(string id, string extra = "", string name = "E", string currency = "EUR", string ownership = """[{"person":"adult-1","share_bp":10000}]""")
            => $$"""{"id":"{{id}}","name":"{{name}}","kind":"savings","currency":"{{currency}}","ownership":{{ownership}}{{extra}}}""";

        // Entity-local rules: the receipt of the batch reports them.
        var local = "[" + string.Join(",",
            Env("e0").Replace("\"kind\":\"savings\"", "\"kind\":\"crypto\""),
            Env("e1", ",\"valuation_cadence\":\"weekly\""),
            Env("e2", name: "  "),
            Env("e3", ",\"account_hint\":\"12345\""),
            Env("e4", ",\"stale_after_days\":0"),
            Env("e5", currency: "EURO"),
            Env("e6", ",\"opened_on\":\"2025-02-30\""),
            Env("e7", ",\"colour\":\"blue\""),
            Env("e8", $",\"institution\":\"{new string('b', 101)}\""),
            Env("e9", $",\"note\":\"{new string('n', 2001)}\"")) + "]";
        var session = await client.StartAsync(app.AdultA, Adults(app));
        var batch = await client.PutAsync(app.AdultA, session.Id, "envelopes", 0, local);

        Assert.False(batch.Accepted);
        await client.CancelRawAsync(app.AdultA, session.Id);
        Assert.Equal(
            [
                ("investments.unknown_kind", "envelopes[0].kind"),
                ("investments.unknown_cadence", "envelopes[1].valuation_cadence"),
                ("investments.invalid_envelope", "envelopes[2].name"),
                ("investments.invalid_envelope", "envelopes[3].account_hint"),
                ("investments.invalid_envelope", "envelopes[4].stale_after_days"),
                (ImportCodes.InvalidValue, "envelopes[5].currency"),
                (ImportCodes.InvalidValue, "envelopes[6].opened_on"),
                (ImportCodes.MalformedEntity, "envelopes[7].colour"),
                ("investments.invalid_envelope", "envelopes[8].institution"),
                ("investments.invalid_envelope", "envelopes[9].note"),
            ],
            batch.Issues.Select(i => (i.Code, i.Path)));

        // Rules that need the household: the validation reports them.
        var global = "[" + string.Join(",",
            Env("g0", ",\"opened_on\":\"2999-01-01\""),
            Env("g1", ownership: """[{"person":"adult-1","share_bp":9000}]"""),
            Env("g2", ownership: """[{"person":"nobody","share_bp":10000}]""")) + "]";
        var validated = await ValidateAsync(client, app, app.AdultA, File(global));

        Assert.Equal("invalid", validated.State);
        Assert.Equal(
            [
                (CoreFactCodes.DateInFuture, "envelopes[0].opened_on"),
                ("core.share_sum", "envelopes[1].ownership"),
                (ImportCodes.UnresolvedReference, "envelopes[2].ownership[0].person"),
            ],
            validated.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)));
    }

    [Fact]
    public async Task An_id_of_another_adults_private_envelope_answers_as_a_new_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, app.AdultB, File("""
            [{"id":"env-secret","name":"Secret","kind":"pea","currency":"EUR","visibility":"private","ownership":[{"person":"adult-2","share_bp":10000}]}]
            """));

        static string Mine(string id) => $$"""
            [{"id":"{{id}}","name":"Mine","kind":"savings","currency":"USD","ownership":[{"person":"adult-1","share_bp":10000}]}]
            """;
        var fresh = await ValidateAsync(client, app, app.AdultA, File(Mine("env-fresh")));
        await client.CancelRawAsync(app.AdultA, fresh.Id);
        var taken = await ValidateAsync(client, app, app.AdultA, File(Mine("env-secret")));

        Assert.Equal("validated", taken.State);
        Assert.Empty(taken.Report!.Issues);
        Assert.Equal(new CountsResponse(1, 0, 0), taken.Report.Sections["envelopes"]);
        Assert.Equal(JsonSerializer.Serialize(fresh.Report), JsonSerializer.Serialize(taken.Report));
        var committed = await client.CommitRawAsync(app.AdultA, taken.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        Assert.Equal(["Mine"], (await ListAsync(app, app.AdultA, archived: true)).Select(e => e.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task Creating_needs_to_be_an_owner()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        var validated = await ValidateAsync(client, app, app.AdultA, File("""
            [{"id":"env-b","name":"B's","kind":"savings","currency":"EUR","ownership":[{"person":"adult-2","share_bp":10000}]}]
            """));

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal((ImportCodes.NotPermitted, "envelopes[0]"), (issue.Code, issue.Path));
        Assert.Empty(await ListAsync(app, app.AdultA, archived: true));
    }

    [Fact]
    public async Task Premiums_before_2017_import_replace_and_export_only_when_set()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        const string life = """
            [{"id":"env-av","name":"AV","kind":"life_insurance","currency":"EUR","premiums_before_2017":"12000.50",
              "ownership":[{"person":"adult-1","share_bp":10000}]},
             {"id":"env-pea","name":"PEA","kind":"pea","currency":"EUR","ownership":[{"person":"adult-1","share_bp":10000}]}]
            """;

        await CommitAsync(client, app, app.AdultA, File(life));
        var av = await StoredAsync(app, await IdOf(app, "AV"));
        Assert.Equal(12000.50m, av.PremiumsBefore2017);
        Assert.Null((await StoredAsync(app, await IdOf(app, "PEA"))).PremiumsBefore2017);

        var exported = (await new Export.ExportClient(app).ExportAllAsync(app.AdultA))["envelopes"];
        Assert.Equal(["AV"], exported.Where(e => e.TryGetProperty("premiums_before_2017", out _)).Select(e => e.GetProperty("name").GetString()).ToList());

        Assert.DoesNotContain("premiums", exported.Single(e => e.GetProperty("name").GetString() == "PEA").GetRawText());
        var again = await CommitAsync(client, app, app.AdultA, File(life)); // an envelope without the field keeps its import hash
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report!.Sections["envelopes"]);

        var changed = life.Replace("12000.50", "9000");
        await CommitAsync(client, app, app.AdultA, File(changed), replace: true);
        Assert.Equal(9000m, (await StoredAsync(app, av.ItemId.Value)).PremiumsBefore2017);

        var session = await client.StartAsync(app.AdultA, Adults(app));
        var batch = await client.PutAsync(app.AdultA, session.Id, "envelopes", 0,
            life.Replace("\"kind\":\"pea\"", "\"kind\":\"pea\",\"premiums_before_2017\":\"1\""));
        await client.CancelRawAsync(app.AdultA, session.Id);
        Assert.Equal([("investments.invalid_envelope", "envelopes[1].premiums_before_2017")], batch.Issues.Select(i => (i.Code, i.Path)).ToList());
    }
}
