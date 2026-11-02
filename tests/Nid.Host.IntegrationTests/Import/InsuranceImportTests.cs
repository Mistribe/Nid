using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Export;
using Nid.Platform.Contracts;
using Nid.RealEstate.Application.Import;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>Issue #208, design §11: the insurance of a subscription travels in the file, in the section <c>subscription_insurances</c>.</summary>
public class InsuranceImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Section = "subscription_insurances";

    private const string Policy = """
        {"id":"ins-1","subscription":"sub-insurance","kind":"home","property":"prop-home","insured_label":"Maison","policy_number":"HX-1",
         "insurer_contact":"01 02 03","guarantees":[{"label":"Fire","ceiling":"100 000 EUR","deductible":"150 EUR"},{"label":"Theft"}]}
        """;

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private static string Subscription(string ownership = """[{"person":"adult-1","share_bp":10000}]""")
        => $$"""[{"id":"sub-insurance","name":"Assurance habitation","ownership":{{ownership}},"auto_renew":true}]""";

    private static string List(params string[] entities) => "[" + string.Join(",", entities) + "]";

    private static Dictionary<string, string> File(string? insurances = null, string? subscriptions = null, string? properties = null)
        => new()
        {
            ["persons"] = GoldenFile.Persons,
            ["subscriptions"] = subscriptions ?? Subscription(),
            ["properties"] = properties ?? List(EstateFile.Home),
            [Section] = insurances ?? List(Policy),
        };

    private static async Task<SessionResponse> ValidateAsync(ImportClient client, NidApp app, IReadOnlyDictionary<string, string> sections, bool replace = false)
    {
        var session = await client.StartAsync(app.AdultA, Adults(app));
        foreach (var (section, entities) in sections)
            Assert.True((await client.PutAsync(app.AdultA, session.Id, section, 0, entities)).Accepted);
        return await client.ValidateAsync(app.AdultA, session.Id, replace);
    }

    private static async Task<SessionResponse> CommitAsync(ImportClient client, NidApp app, IReadOnlyDictionary<string, string> sections, bool replace = false)
    {
        var validated = await ValidateAsync(client, app, sections, replace);
        Assert.True(validated.State == "validated", string.Join("; ", validated.Report!.Issues.Select(i => $"{i.Code} at {i.Path}")));
        var committed = await client.CommitRawAsync(app.AdultA, validated.Id, replace);
        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        return await client.GetAsync(app.AdultA, validated.Id);
    }

    private static IEnumerable<(string Code, string Path)> Errors(SessionResponse session)
        => session.Report!.Issues.Where(i => i.Severity == "error").Select(i => (i.Code, i.Path));

    private static async Task<SubscriptionInsuranceRowCopy> StoredAsync(NidApp app)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var row = await subs.Insurances.AsNoTracking().Include(i => i.Guarantees).SingleAsync();
        return new SubscriptionInsuranceRowCopy(row.ItemId, row.Kind, row.PropertyId, row.PolicyNumber, row.ImportRef, row.Guarantees.Count);
    }

    private sealed record SubscriptionInsuranceRowCopy(Guid ItemId, string Kind, Guid? PropertyId, string? PolicyNumber, string? ImportRef, int Guarantees);

    [Fact]
    public async Task Round_trip_keeps_the_property_link()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Anna");
        await RoundTripTests.CommitAsync(source, source.AdultA, File());

        var first = await new ExportClient(source).ExportAllAsync(source.AdultA);
        var exported = Assert.Single(first[Section]);
        Assert.Equal(("ins-1", "sub-insurance", "home", "prop-home"),
            (exported.GetProperty("id").GetString(), exported.GetProperty("subscription").GetString(), exported.GetProperty("kind").GetString(), exported.GetProperty("property").GetString()));
        Assert.Equal(["Fire", "Theft"], exported.GetProperty("guarantees").EnumerateArray().Select(g => g.GetProperty("label").GetString()));

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Zoé");
        await RoundTripTests.CommitAsync(target, target.AdultA, RoundTripTests.AsFile(first));

        var stored = await StoredAsync(target);
        Assert.NotNull(stored.PropertyId);
        Assert.Equal(("home", "HX-1", "ins-1", 2), (stored.Kind, stored.PolicyNumber, stored.ImportRef, stored.Guarantees));
        var second = await new ExportClient(target).ExportAllAsync(target.AdultA);
        Assert.Equal(JsonSerializer.Serialize(first[Section]), JsonSerializer.Serialize(second[Section]));
    }

    [Fact]
    public async Task A_file_without_the_section_stays_valid()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var file = File();
        file.Remove(Section);

        var committed = await CommitAsync(new ImportClient(app), app, file);

        Assert.True(!committed.Report!.Sections.TryGetValue(Section, out var counts) || counts == new CountsResponse(0, 0, 0));
    }

    [Fact]
    public async Task Property_of_another_file_is_unresolved()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await ValidateAsync(new ImportClient(app), app, File(List(Policy.Replace("prop-home", "prop-elsewhere"))));

        Assert.Equal([(ImportCodes.UnresolvedReference, "subscription_insurances[0].property")], Errors(validated));
    }

    [Fact]
    public async Task Unknown_subscription_is_unresolved()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await ValidateAsync(new ImportClient(app), app, File(List(Policy.Replace("sub-insurance", "sub-elsewhere"))));

        Assert.Equal([(ImportCodes.UnresolvedReference, "subscription_insurances[0].subscription")], Errors(validated));
    }

    [Fact]
    public async Task Second_insurance_for_one_subscription_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await ValidateAsync(new ImportClient(app), app, File(List(Policy, Policy.Replace("ins-1", "ins-2"))));

        Assert.Equal([(ImportCodes.DuplicateId, "subscription_insurances[1].subscription")], Errors(validated));
    }

    [Fact]
    public async Task Failed_subscription_is_skipped_not_reported_twice()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await ValidateAsync(new ImportClient(app), app,
            File(subscriptions: Subscription("""[{"person":"adult-1","share_bp":5000}]""")));

        Assert.Single(Errors(validated), e => e.Path.StartsWith("subscriptions[0]", StringComparison.Ordinal));
        Assert.DoesNotContain(Errors(validated), e => e.Path.StartsWith(Section, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Record_rules_are_refused_at_their_own_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, Adults(app));

        var batch = await client.PutAsync(app.AdultA, session.Id, Section, 0,
            List(Policy.Replace("\"home\"", "\"boat\""), Policy.Replace("ins-1", "ins-2").Replace("\"Theft\"", "\" \"")));

        Assert.False(batch.Accepted);
        Assert.Equal(
            [(ImportCodes.InvalidValue, "subscription_insurances[0].kind"), ("subscriptions.guarantee_label", "subscription_insurances[1].guarantees[1].label")],
            batch.Issues.Select(i => (i.Code, i.Path)));
    }

    [Fact]
    public async Task Reimport_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var first = await CommitAsync(client, app, File());
        Assert.Equal(new CountsResponse(1, 0, 0), first.Report!.Sections[Section]);

        var again = await CommitAsync(client, app, File());

        Assert.Equal(new CountsResponse(0, 1, 0), again.Report!.Sections[Section]);
    }

    [Fact]
    public async Task Replace_updates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, File());
        var changed = File(List(Policy.Replace("HX-1", "HX-2").Replace(",{\"label\":\"Theft\"}", "")));

        var refused = await ValidateAsync(client, app, changed);
        Assert.Equal([(ImportCodes.Conflict, "subscription_insurances[0]")], Errors(refused));
        (await client.CancelRawAsync(app.AdultA, refused.Id)).EnsureSuccessStatusCode();
        Assert.Equal("HX-1", (await StoredAsync(app)).PolicyNumber);

        var replaced = await CommitAsync(client, app, changed, replace: true);

        Assert.Equal(new CountsResponse(0, 0, 0, 1), replaced.Report!.Sections[Section]);
        var stored = await StoredAsync(app);
        Assert.Equal(("HX-2", 1), (stored.PolicyNumber, stored.Guarantees));
    }

    [Fact]
    public async Task An_edit_in_the_ui_survives_the_same_file_again()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, File());
        var id = new ItemId((await StoredAsync(app)).ItemId);
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/subscriptions/{id.Value}/insurance",
            new { kind = "home", insuredLabel = "Maison", policyNumber = "UI-9", guarantees = new[] { new { label = "Fire" } } });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A UI edit keeps the provenance: the hash is the file's, so the same file is unchanged and the edit stays.
        var again = await CommitAsync(client, app, File(), replace: true);

        Assert.Equal(new CountsResponse(0, 1, 0), again.Report!.Sections[Section]);
        Assert.Equal("UI-9", (await StoredAsync(app)).PolicyNumber);
    }

    [Fact]
    public async Task An_insurance_set_in_the_ui_is_not_overwritten_by_another_file_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, File("[]"));
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            var id = (await subs.Subscriptions.AsNoTracking().SingleAsync(s => s.ImportRef == "sub-insurance", cancellationToken: TestContext.Current.CancellationToken)).ItemId;
            var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/subscriptions/{id}/insurance",
                new { kind = "car", policyNumber = "UI-1", guarantees = Array.Empty<object>() });
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var refused = await ValidateAsync(client, app, File());
        Assert.Equal([(ImportCodes.Conflict, "subscription_insurances[0]")], Errors(refused));
        (await client.CancelRawAsync(app.AdultA, refused.Id)).EnsureSuccessStatusCode();

        await CommitAsync(client, app, File(), replace: true);
        Assert.Equal(("home", "ins-1"), ((await StoredAsync(app)).Kind, (await StoredAsync(app)).ImportRef));
    }

    [Fact]
    public async Task Hidden_property_exports_without_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Assurance de B", app.AdultB, Visibility.Shared);
        var hidden = await app.NewPropertyAsync("Maison secrete", Visibility.Private, (app.AdultB, 10_000));
        var put = await app.RawAsync(app.AdultB, HttpMethod.Put, $"/api/v1/subscriptions/{subscription.Value}/insurance",
            new { kind = "home", propertyId = hidden.Value, guarantees = new[] { new { label = "Fire" } } });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var forA = await new ExportClient(app).ExportAllAsync(app.AdultA);

        var insurance = Assert.Single(forA[Section]);
        Assert.Equal(JsonValueKind.Null, insurance.GetProperty("property").ValueKind);
        Assert.Empty(forA["properties"]);
        Assert.DoesNotContain("Maison secrete", JsonSerializer.Serialize(forA));
        var forB = await new ExportClient(app).ExportAllAsync(app.AdultB);
        Assert.Equal(Assert.Single(forB["properties"]).GetProperty("id").GetString(), Assert.Single(forB[Section]).GetProperty("property").GetString());
    }

    [Fact]
    public async Task Refused_property_is_skipped_not_reported_twice()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var short5000 = EstateFile.Home.Replace("""{"person":"adult-1","share_bp":5000},{"person":"adult-2","share_bp":5000}""", """{"person":"adult-1","share_bp":5000}""");

        var validated = await ValidateAsync(new ImportClient(app), app, File(properties: List(short5000)));

        var errors = Errors(validated).ToList();
        Assert.Single(errors);
        Assert.StartsWith("properties[0]", errors[0].Path, StringComparison.Ordinal);
        Assert.DoesNotContain(errors, e => e.Path.StartsWith(Section, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ref_held_on_another_subscription_is_refused_at_its_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await CommitAsync(client, app, File());
        var second = """[{"id":"sub-other","name":"Assurance auto","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":true}]""";
        var subscriptions = Subscription().TrimEnd(']') + "," + second.TrimStart('[');

        // ins-1 is held by sub-insurance (same importer): it cannot move to sub-other.
        var validated = await ValidateAsync(client, app, File(List(Policy.Replace("sub-insurance", "sub-other")), subscriptions));

        Assert.Equal([(ImportCodes.InvalidValue, "subscription_insurances[0].subscription")], Errors(validated));
    }

    [Fact]
    public async Task A_subscription_the_importer_reads_but_cannot_write_is_not_permitted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        // B imports the subscription as its own; the same record, unchanged for A, is a subscription A reads (shared) but does not own.
        var sessionB = await client.StartAsync(app.AdultB, new Dictionary<string, Guid> { ["adult-1"] = app.AdultB.Value, ["adult-2"] = app.AdultA.Value });
        foreach (var (section, entities) in new Dictionary<string, string> { ["persons"] = GoldenFile.Persons, ["subscriptions"] = Subscription() })
            Assert.True((await client.PutAsync(app.AdultB, sessionB.Id, section, 0, entities)).Accepted);
        Assert.Equal("validated", (await client.ValidateAsync(app.AdultB, sessionB.Id)).State);
        Assert.True((await client.CommitRawAsync(app.AdultB, sessionB.Id)).IsSuccessStatusCode);

        var file = File(subscriptions: Subscription());
        file.Remove("properties");
        file[Section] = List(Policy.Replace(",\"property\":\"prop-home\"", ""));
        var validated = await ValidateAsync(client, app, file);

        Assert.Equal([(ImportCodes.NotPermitted, "subscription_insurances[0]")], Errors(validated));
    }

    [Fact]
    public async Task Section_runs_after_properties()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sections = scope.ServiceProvider.GetServices<IImportSection>().ToList();

        var insurances = Assert.Single(sections, s => s.Sections.Contains(Section));
        var estate = Assert.Single(sections.OfType<RealEstateImportSection>());

        Assert.Equal(45, insurances.Order);
        Assert.True(insurances.Order > estate.Order);
    }
}
