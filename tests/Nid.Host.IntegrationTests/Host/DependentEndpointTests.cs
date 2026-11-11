using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>#156: Réglages › Personnes. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class DependentEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Persons = "/api/v1/core/persons";
    private const string Dependents = "/api/v1/core/persons/dependents";

    private static async Task<JsonElement> PersonAsync(NidApp app, Guid id)
        => (await app.GetAsync<JsonElement>(app.AdultA, Persons)).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_person_reads_with_age_retirement_month_and_sign_in()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Persons}/{app.AdultB.Value}/retirement",
            new { birthDate = "1984-05-20", retirementAge = 64, retirementMonths = 3 })).EnsureSuccessStatusCode();
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Persons}/{app.Dependent.Value}/retirement",
            new { birthDate = "2015-10-16", retirementAge = 64 })).EnsureSuccessStatusCode();

        var adult = await PersonAsync(app, app.AdultB.Value);
        Assert.Equal(41, adult.GetProperty("age").GetInt32());
        Assert.Equal("2048-08", adult.GetProperty("retirementOn").GetString());
        Assert.True(adult.GetProperty("signedIn").GetBoolean());

        var child = await PersonAsync(app, app.Dependent.Value);
        Assert.Equal(9, child.GetProperty("age").GetInt32()); // ten tomorrow
        Assert.Equal(JsonValueKind.Null, child.GetProperty("retirementOn").ValueKind);
        Assert.False(child.GetProperty("signedIn").GetBoolean());

        var undated = await PersonAsync(app, app.AdultA.Value);
        Assert.Equal(JsonValueKind.Null, undated.GetProperty("age").ValueKind);
        Assert.Equal(JsonValueKind.Null, undated.GetProperty("retirementOn").ValueKind);
    }

    [Fact]
    public async Task A_child_is_added_with_a_birth_date()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, Dependents, new { displayName = "Léa", birthDate = "2020-03-01" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ProblemAsync(response);
        Assert.Equal("2020-03-01", created.GetProperty("birthDate").GetString());
        Assert.Equal(5, created.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task A_birth_date_in_the_future_adds_no_child()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var before = (await app.GetAsync<JsonElement>(app.AdultA, Persons)).GetArrayLength();

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Dependents, new { displayName = "Léa", birthDate = "2030-01-01" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("core.invalid_birth_date", problem.GetProperty("code").GetString());
        Assert.Equal("birthDate", problem.GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(before, (await app.GetAsync<JsonElement>(app.AdultA, Persons)).GetArrayLength());
    }

    [Fact]
    public async Task Any_adult_renames_a_child()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Put, $"{Dependents}/{app.Dependent.Value}/name", new { displayName = "  Léa Martin " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Léa Martin", (await PersonAsync(app, app.Dependent.Value)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task An_empty_name_is_refused_at_displayName()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Dependents}/{app.Dependent.Value}/name", new { displayName = " " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("core.name_required", problem.GetProperty("code").GetString());
        Assert.Equal("displayName", problem.GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task An_adult_or_an_unknown_id_is_neither_renamed_nor_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var id in new[] { app.AdultB.Value, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Dependents}/{id}/name", new { displayName = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Dependents}/{id}")).StatusCode);
        }
        Assert.NotEqual("X", (await PersonAsync(app, app.AdultB.Value)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Any_adult_removes_a_child_nothing_names()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Dependents}/{app.Dependent.Value}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain((await app.GetAsync<JsonElement>(app.AdultA, Persons)).EnumerateArray(), p => p.GetProperty("id").GetGuid() == app.Dependent.Value);
    }

    [Fact]
    public async Task A_child_who_owns_an_item_is_not_removed_and_the_refusal_names_no_item()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, "Maison secrète", "shared", app.AdultA, app.Dependent));

        var response = await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Dependents}/{app.Dependent.Value}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("core.person_in_use", problem.GetProperty("code").GetString());
        Assert.Equal(["ownerships"], problem.GetProperty("uses").EnumerateArray().Select(u => u.GetString()).ToArray());
        Assert.DoesNotContain("secrète", body, StringComparison.Ordinal);
        await PersonAsync(app, app.Dependent.Value);
    }

    [Fact]
    public async Task A_child_with_a_childcare_contract_is_not_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var schedule = new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "17:30", lunch = true }).ToArray();
        var arrangement = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/childcare", new Dictionary<string, object?>
        {
            ["name"] = "Garde", ["childId"] = app.Dependent.Value,
            ["carer"] = new { name = "Martine", pajemploi = "P123", approval = (string?)null },
            ["terms"] = new Dictionary<string, object?>
            {
                ["from"] = "2025-09-01", ["netHourlyRate"] = "4.30", ["grossHourlyRate"] = null, ["overtimePct"] = "10", ["upkeepPer9h"] = "3.80",
                ["lunchPrice"] = "3.20", ["weekTypes"] = new[] { new { name = "Semaine type", weeks = 46, schedule } },
            },
            ["gaps"] = Array.Empty<object>(), ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, arrangement.StatusCode);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Dependents}/{app.Dependent.Value}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["childcare"], (await ProblemAsync(response)).GetProperty("uses").EnumerateArray().Select(u => u.GetString()).ToArray());
    }

    [Fact]
    public async Task A_child_who_uses_a_subscription_is_not_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSpotifyGoldenCaseAsync(app.AdultA);
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            subs.Add(new Nid.Subscriptions.Infrastructure.Persistence.SubscriptionUserRow { ItemId = subscription.Value, PersonId = app.Dependent.Value });
            await subs.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Dependents}/{app.Dependent.Value}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["subscriptions"], (await ProblemAsync(response)).GetProperty("uses").EnumerateArray().Select(u => u.GetString()).ToArray());
    }

    [Fact]
    public async Task A_child_with_a_document_filed_under_them_is_not_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var documents = PostgresFixture.NewDocumentsContext(app.ConnectionString))
        {
            var id = Guid.NewGuid();
            var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
            documents.Documents.Add(new Nid.Documents.Infrastructure.Persistence.DocumentRow
            {
                Id = id, Sha256 = new string('a', 64), SizeBytes = 1, MediaType = "application/pdf", OriginalFilename = "carnet.pdf", Title = "Carnet de santé",
                UploadedByPersonId = app.AdultA.Value, UploadedAt = now,
            });
            documents.Links.Add(new Nid.Documents.Infrastructure.Persistence.DocumentLinkRow
            {
                DocumentId = id, TargetKind = "core.person", TargetId = app.Dependent.Value, Role = "other", LinkedByPersonId = app.AdultA.Value, LinkedAt = now,
            });
            await documents.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Dependents}/{app.Dependent.Value}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["documents"], (await ProblemAsync(response)).GetProperty("uses").EnumerateArray().Select(u => u.GetString()).ToArray());
    }

    [Fact]
    public async Task A_child_named_by_a_projection_scenario_is_not_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Persons}/{app.Dependent.Value}/retirement",
            new { birthDate = "2015-06-01", retirementAge = 64 })).EnsureSuccessStatusCode();
        var studies = new { id = Guid.NewGuid(), kind = "income_change", label = "Études", start = new { person = app.Dependent.Value, age = 18 }, monthlyAmount = "-500" };
        var scenario = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/scenarios/projection", new
        {
            name = "Études", visibility = "private", scope = "me",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            document = new { version = 1, horizon = "10y", assumptions = new { }, events = new[] { studies } },
        });
        Assert.True(scenario.StatusCode == HttpStatusCode.Created, await scenario.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var response = await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Dependents}/{app.Dependent.Value}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["scenarios"], (await ProblemAsync(response)).GetProperty("uses").EnumerateArray().Select(u => u.GetString()).ToArray());
    }
}
