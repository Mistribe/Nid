using System.Net;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

public class SubscriptionsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record MoneyResponse(decimal Amount, string Currency);
    public sealed record SummaryResponse(Guid Id, string DisplayName);
    public sealed record ListResponse(List<SummaryResponse> Items);
    public sealed record CoverageResponse(string Start, string EndBefore, string Source);
    public sealed record DetailResponse(
        Guid Id, string DisplayName, string? ExpiresOn, string? NextChargeOn, MoneyResponse? NextChargeAmount,
        List<MoneyResponse> CashCost, List<MoneyResponse> AmortizedCost, int FreeDays, List<CoverageResponse> Coverage);

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, IReadOnlyDictionary<string, string> sections)
    {
        var validated = await client.RunAsync(app.AdultA, Adults(app), sections);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        var committed = await client.CommitRawAsync(app.AdultA, validated.Id);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(app.AdultA, validated.Id);
    }

    private static async Task<Guid> IdOf(NidApp app, string name)
        => (await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me")).Items.Single(i => i.DisplayName == name).Id;

    [Fact]
    public async Task The_three_worked_examples_come_out_of_the_API_as_documented()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var committed = await CommitAsync(new ImportClient(app), app, GoldenFile.Sections());

        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report!.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(2, 0, 0), committed.Report.Sections["billing_phases"]);
        Assert.Equal(new CountsResponse(3, 0, 0), committed.Report.Sections["purchases"]);

        var spotify = await app.GetAsync<DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{await IdOf(app, "Spotify Premium Duo")}?horizon=2026-06-01");
        var gift = Assert.Single(spotify.Coverage, c => c.Source == "gift");
        Assert.Equal(("2025-10-28", "2026-01-28"), (gift.Start, gift.EndBefore));
        Assert.Equal("2026-01-28", spotify.NextChargeOn);
        Assert.Equal(17.20m, spotify.NextChargeAmount!.Amount);

        var nordvpn = await app.GetAsync<DetailResponse>(app.AdultA,
            $"/api/v1/subscriptions/{await IdOf(app, "NordVPN")}?from=2025-06-22&to=2025-06-23&horizon=2030-01-01");
        Assert.Equal(179.86m, Assert.Single(nordvpn.CashCost, m => m.Currency == "EUR").Amount);
        Assert.Equal("2027-12-22", nordvpn.ExpiresOn);

        var wow = await app.GetAsync<DetailResponse>(app.AdultA,
            $"/api/v1/subscriptions/{await IdOf(app, "World of Warcraft")}?from=2024-11-01&to=2025-01-01");
        Assert.Equal(88.99m, Assert.Single(wow.CashCost, m => m.Currency == "EUR").Amount);
        Assert.DoesNotContain(wow.AmortizedCost, m => m.Currency == "EUR");
        Assert.Equal(30, wow.FreeDays);
    }

    [Fact]
    public async Task Importing_the_same_file_again_changes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());
        int items;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            items = await core.Items.CountAsync(cancellationToken: TestContext.Current.CancellationToken);

        var again = await CommitAsync(client, app, GoldenFile.Sections());

        Assert.Equal(new CountsResponse(0, 3, 0), again.Report!.Sections["subscriptions"]);
        Assert.Equal(new CountsResponse(0, 2, 0), again.Report.Sections["billing_phases"]);
        Assert.Equal(new CountsResponse(0, 3, 0), again.Report.Sections["purchases"]);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Equal(items, await core.Items.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_same_id_with_other_content_is_a_conflict_that_blocks_the_commit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        var changed = GoldenFile.With("subscriptions", GoldenFile.Subscriptions.Replace("Spotify Premium Duo", "Spotify Family"));
        var validated = await client.RunAsync(app.AdultA, Adults(app), changed);

        Assert.Equal("invalid", validated.State);
        Assert.Equal(new CountsResponse(0, 2, 1), validated.Report!.Sections["subscriptions"]);
        var conflict = Assert.Single(validated.Report.Issues, i => i.Code == ImportCodes.Conflict);
        Assert.Equal("subscriptions[0]", conflict.Path);
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, validated.Id)).StatusCode);
    }

    [Fact]
    public async Task A_later_file_can_add_a_purchase_to_an_imported_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        var extra = GoldenFile.Purchases.TrimEnd().TrimEnd(']') + """
            ,{"id":"pu-extra","date":"2026-02-01","label":"Carte cadeau","kind":"gift",
              "price":{"amount":"0","currency":"EUR"},"payer":{"type":"external"},
              "grants":[{"subscription_id":"sub-spotify","duration":{"count":1,"unit":"month"},"source":"gift"}]}]
            """;
        var again = await CommitAsync(client, app, GoldenFile.With("purchases", extra));

        Assert.Equal(new CountsResponse(1, 3, 0), again.Report!.Sections["purchases"]);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.True(await subs.Purchases.AnyAsync(p => p.ImportRef == "pu-extra", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A file that adds to a subscription imported before is checked against what is stored, not only against
    /// itself: the later file may repeat the stored phases or leave them out, and the new one overlapping the
    /// stored open phase is refused either way, at the new phase.
    /// </summary>
    [Theory]
    [InlineData(true, "billing_phases[2]")]
    [InlineData(false, "billing_phases[0]")]
    public async Task A_new_phase_overlapping_a_stored_one_is_refused_where_it_is(bool repeatStored, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, GoldenFile.Sections());

        const string extra = """
            [{"id":"ph-spotify-3","subscription_id":"sub-spotify","starts_on":"2025-07-28","ends_on":null,
              "price":{"amount":"19.99","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}]
            """;
        var phases = repeatStored ? Append(GoldenFile.Phases, extra) : extra;
        var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.With("billing_phases", phases));

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        Assert.Equal(("subscriptions.phase_overlap", path), (issue.Code, issue.Path));
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultA, validated.Id)).StatusCode);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(2, await subs.Phases.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// B re-imports A's shared subscription unchanged — allowed: nothing is created, and B can see it — and adds
    /// a phase and a purchase to it, which B may not write (a shared item owned by A alone is written by A).
    /// The creation rule is not what refuses B here: it applies only to a record that creates an item.
    /// </summary>
    [Fact]
    public async Task An_adult_cannot_add_to_an_imported_subscription_the_other_adult_writes_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var subscription = """[{"id":"sub-a","name":"A's","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";
        var phase = """{"id":"ph-a-1","subscription_id":"sub-a","starts_on":"2025-01-01","ends_on":"2025-02-01","price":{"amount":"5","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}""";
        await CommitAsync(client, app, new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons, ["subscriptions"] = subscription, ["billing_phases"] = $"[{phase}]",
        });

        var validated = await client.RunAsync(app.AdultB, Adults(app), new Dictionary<string, string>
        {
            ["persons"] = GoldenFile.Persons,
            ["subscriptions"] = subscription,
            ["billing_phases"] = "[" + phase + "," + """{"id":"ph-a-2","subscription_id":"sub-a","starts_on":"2025-03-01","ends_on":"2025-04-01","price":{"amount":"5","currency":"EUR"},"every":{"count":1,"unit":"month"},"payer":{"type":"household"}}]""",
            ["purchases"] = """[{"id":"pu-b","date":"2025-05-01","label":"B","kind":"gift","price":{"amount":"0","currency":"EUR"},"payer":{"type":"external"},"grants":[{"subscription_id":"sub-a","duration":{"count":1,"unit":"month"},"source":"gift"}]}]""",
        });

        Assert.Equal("invalid", validated.State);
        Assert.Equal(new CountsResponse(0, 1, 0), validated.Report!.Sections["subscriptions"]);
        Assert.Equal(
            [(ImportCodes.NotPermitted, "billing_phases[1].subscription_id"), (ImportCodes.NotPermitted, "purchases[0].grants")],
            validated.Report.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path)).OrderBy(x => x.Path).ToList());
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultB, validated.Id)).StatusCode);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(1, await subs.Phases.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await subs.Purchases.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_adult_cannot_import_an_item_owned_by_the_other_adult_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = """[{"id":"sub-theirs","name":"Theirs","ownership":[{"person":"adult-2","share_bp":10000}],"visibility":"private","auto_renew":true}]""";

        var validated = await new ImportClient(app).RunAsync(app.AdultA, Adults(app),
            new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = theirs });

        var issue = Assert.Single(validated.Report!.Issues);
        Assert.Equal((ImportCodes.NotPermitted, "subscriptions[0]"), (issue.Code, issue.Path));
    }

    /// <summary>
    /// A imports and commits a subscription private to them. B later imports the identical record. A's row is
    /// invisible to B, so B's entry is a new one (spec 2026-09-25 §4). What refuses it is the creation rule, since
    /// the record is owned by A alone, with exactly the answer an unused id gets, never "imported before".
    /// </summary>
    [Fact]
    public async Task B_cannot_reimport_a_subscription_committed_private_to_A()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var subscription = """[{"id":"sub-private","name":"A's private","visibility":"private","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";
        await CommitAsync(client, app, new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = subscription });

        var validated = await client.RunAsync(app.AdultB, Adults(app),
            new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = subscription });

        Assert.Equal("invalid", validated.State);
        var issue = Assert.Single(validated.Report!.Issues);
        Assert.Equal((ImportCodes.NotPermitted, "subscriptions[0]", "An item is imported by one of its owners, unless every owner is a dependent."),
            (issue.Code, issue.Path, issue.Message));
        Assert.Equal(HttpStatusCode.Conflict, (await client.CommitRawAsync(app.AdultB, validated.Id)).StatusCode);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.Equal(1, await subs.Subscriptions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_subscription_owned_by_a_new_dependent_is_created_with_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var persons = """[{"ref":"adult-1","kind":"adult","display_name":"A"},{"ref":"kid","kind":"dependent","display_name":"Léa"}]""";
        var theirs = """[{"id":"sub-kid","name":"Kid's","ownership":[{"person":"kid","share_bp":10000}],"auto_renew":true}]""";

        await CommitAsync(new ImportClient(app), app, new Dictionary<string, string> { ["persons"] = persons, ["subscriptions"] = theirs });

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var lea = await core.Persons.SingleAsync(p => p.DisplayName == "Léa", cancellationToken: TestContext.Current.CancellationToken);
        var item = await core.Items.Include(i => i.Owners).SingleAsync(i => i.DisplayName == "Kid's", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(lea.Id, Assert.Single(item.Owners).PersonId);
    }

    public sealed record BlockingCase(string Section, string Entities, string Code, string Path, bool OnReceipt);

    private const string Owner = "\"ownership\":[{\"person\":\"adult-1\",\"share_bp\":10000}]";

    /// <summary>
    /// Validate-stage cases add one entity to the golden section rather than replace it: replacing it would
    /// orphan every reference the other sections make to it, and the report would carry more than one error.
    /// </summary>
    private static string Append(string golden, string entities)
        => golden.TrimEnd().TrimEnd(']') + "," + entities.TrimStart().TrimStart('[');

    private static readonly Dictionary<string, BlockingCase> Cases = new()
    {
        ["unknown field"] = new("subscriptions", $$"""[{"id":"sub-x","name":"X",{{Owner}},"auto_renew":true,"colour":"red"}]""", ImportCodes.MalformedEntity, "subscriptions[0]", true),
        ["null owner"] = new("subscriptions", """[{"id":"sub-x","name":"X","ownership":[null],"auto_renew":true}]""", ImportCodes.MalformedEntity, "subscriptions[0].ownership[0]", true),
        ["null service user"] = new("subscriptions", $$"""[{"id":"sub-x","name":"X",{{Owner}},"service_users":[null],"auto_renew":true}]""", ImportCodes.MalformedEntity, "subscriptions[0].service_users[0]", true),
        ["null grant"] = new("purchases", """[{"id":"pu-x","date":"2025-01-01","label":"X","kind":"gift","price":{"amount":"0","currency":"EUR"},"payer":{"type":"external"},"grants":[null]}]""", ImportCodes.MalformedEntity, "purchases[0].grants[0]", true),
        ["duplicate service user"] = new("subscriptions", $$"""[{"id":"sub-x","name":"X",{{Owner}},"service_users":["adult-1","adult-1"],"auto_renew":true}]""", ImportCodes.DuplicateId, "subscriptions[0].service_users[1]", true),
        ["blank name"] = new("subscriptions", $$"""[{"id":"sub-x","name":"  ",{{Owner}},"auto_renew":true}]""", ImportCodes.InvalidValue, "subscriptions[0].name", true),
        ["name over 100 characters"] = new("subscriptions", $$"""[{"id":"sub-x","name":"{{new string('x', 101)}}",{{Owner}},"auto_renew":true}]""", ImportCodes.InvalidValue, "subscriptions[0].name", true),
        ["bad id"] = new("subscriptions", $$"""[{"id":"Sub_X","name":"X",{{Owner}},"auto_renew":true}]""", ImportCodes.InvalidId, "subscriptions[0].id", true),
        ["zero count"] = new("billing_phases", Phase(every: """{"count":0,"unit":"month"}"""), "core.duration_count", "billing_phases[0].every.count", true),
        ["unknown unit"] = new("billing_phases", Phase(every: """{"count":1,"unit":"fortnight"}"""), ImportCodes.InvalidValue, "billing_phases[0].every.unit", true),
        ["bad currency"] = new("billing_phases", Phase(price: """{"amount":"1","currency":"EURO"}"""), ImportCodes.InvalidValue, "billing_phases[0].price.currency", true),
        ["bad date"] = new("billing_phases", Phase(startsOn: "2025-02-30"), ImportCodes.InvalidValue, "billing_phases[0].starts_on", true),
        ["empty phase"] = new("billing_phases", Phase(startsOn: "2025-01-01", endsOn: "\"2025-01-01\""), "subscriptions.phase_range", "billing_phases[0]", true),
        ["negative gift"] = new("purchases", Purchase(kind: "gift", amount: "-1"), "subscriptions.price_negative", "purchases[0]", true),
        ["allocated outside a bundle"] = new("purchases", Purchase(kind: "prepaid_term", allocated: "\"1\""), "subscriptions.allocated_not_bundle", "purchases[0]", true),
        ["allocated above the price"] = new("purchases", Purchase(kind: "bundle", allocated: "\"100\""), "subscriptions.allocated_range", "purchases[0]", true),
        ["duplicate id"] = new("subscriptions", Append(GoldenFile.Subscriptions, $$"""[{"id":"sub-spotify","name":"Again",{{Owner}},"auto_renew":true}]"""), ImportCodes.DuplicateId, "subscriptions[3].id", false),
        ["unknown owner"] = new("subscriptions", Append(GoldenFile.Subscriptions, """[{"id":"sub-x","name":"X","ownership":[{"person":"adult-9","share_bp":10000}],"auto_renew":true}]"""), ImportCodes.UnresolvedReference, "subscriptions[3].ownership[0].person", false),
        ["shares short of 10000"] = new("subscriptions", Append(GoldenFile.Subscriptions, """[{"id":"sub-x","name":"X","ownership":[{"person":"adult-1","share_bp":9000}],"auto_renew":true}]"""), "core.share_sum", "subscriptions[3].ownership", false),
        ["private with two owners"] = new("subscriptions", Append(GoldenFile.Subscriptions, """[{"id":"sub-x","name":"X","visibility":"private","ownership":[{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}],"auto_renew":true}]"""), "core.private_single_owner", "subscriptions[3].ownership", false),
        ["overlapping phases"] = new("billing_phases", GoldenFile.Phases.Replace("\"ends_on\":\"2025-06-28\"", "\"ends_on\":\"2025-07-28\""), "subscriptions.phase_overlap", "billing_phases[0]", false),
        ["unknown subscription"] = new("billing_phases", Append(GoldenFile.Phases, Phase(subscription: "sub-none")), ImportCodes.UnresolvedReference, "billing_phases[2].subscription_id", false),
        ["unresolved adult"] = new("persons", Append(GoldenFile.Persons, """[{"ref":"adult-3","kind":"adult","display_name":"C"}]"""), ImportCodes.AdultUnresolved, "persons[2]", false),
    };

    public static TheoryData<string> CaseNames() => new(Cases.Keys);

    /// <summary>One file per blocking error of import-format.md §7, each reported with its code and exact path.</summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Every_blocking_error_is_reported_where_it_is(string name)
    {
        var @case = Cases[name];
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        IssueResponse issue;
        if (@case.OnReceipt)
        {
            var session = await client.StartAsync(app.AdultA, Adults(app));
            var batch = await client.PutAsync(app.AdultA, session.Id, @case.Section, 0, @case.Entities);
            Assert.False(batch.Accepted);
            issue = Assert.Single(batch.Issues, i => i.Severity == "error");
        }
        else
        {
            var validated = await client.RunAsync(app.AdultA, Adults(app), GoldenFile.With(@case.Section, @case.Entities));
            Assert.Equal("invalid", validated.State);
            issue = Assert.Single(validated.Report!.Issues, i => i.Severity == "error");
        }

        Assert.Equal(@case.Code, issue.Code);
        if (@case.Code == ImportCodes.MalformedEntity && !@case.Path.Contains('.'))
            Assert.StartsWith(@case.Path, issue.Path); // the member comes from System.Text.Json's own path
        else
            Assert.Equal(@case.Path, issue.Path);
    }

    private static string Phase(string subscription = "sub-spotify", string startsOn = "2023-01-01", string endsOn = "\"2023-06-01\"",
                                string price = """{"amount":"1","currency":"EUR"}""", string every = """{"count":1,"unit":"month"}""")
        => $$$"""[{"id":"ph-x","subscription_id":"{{{subscription}}}","starts_on":"{{{startsOn}}}","ends_on":{{{endsOn}}},"price":{{{price}}},"every":{{{every}}},"payer":{"type":"household"}}]""";

    private static string Purchase(string kind, string amount = "88.99", string allocated = "null")
        => $$"""[{"id":"pu-x","date":"2025-01-01","label":"X","kind":"{{kind}}","price":{"amount":"{{amount}}","currency":"EUR"},"payer":{"type":"household"},"allocated_amount":{{allocated}},"grants":[{"subscription_id":"sub-spotify","duration":{"count":1,"unit":"month"},"source":"paid"}]}]""";
}
