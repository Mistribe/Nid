using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>
/// Rental spec §3.3–3.4: a revision offered, accepted, undone, listed and named in the overview. Today is 2026-10-15; the chain
/// starts on 2025-09-01 at 800 with quarter 2, so the anniversary is 2026-09-01 and the rent becomes 800 × 148.37 ÷ 146.68.
/// </summary>
public class RentRevisionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 15);

    private const string NotDue = "realestate.revision_not_due", EffectiveOn = "realestate.revision_effective_on",
        ForbiddenDpe = "realestate.revision_forbidden_dpe", UndoRefused = "realestate.revision_undo_refused";

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    /// <summary>The app on <see cref="Today"/>, with 2025-Q2 and 2026-Q2 stored as INSEE published them.</summary>
    private static async Task<NidApp> StartAsync(PostgresFixture fixture)
    {
        var app = await NidApp.StartAsync(fixture,
            services => services.AddSingleton<IClock>(new FixedClock(Today.At(new LocalTime(12, 0)).InUtc().ToInstant())));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IIrlStore>();
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2025, 2), 146.68m, new LocalDate(2025, 7, 16), new LocalDate(2025, 7, 21), "insee"), default);
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2026, 2), 148.37m, new LocalDate(2026, 7, 12), new LocalDate(2026, 7, 13), "insee"), default);
        return app;
    }

    /// <summary>A property of <paramref name="actor"/> (A by default) with one indexed tenancy; its id and the tenancy's.</summary>
    private static async Task<(Guid Property, Guid Tenancy)> LetAsync(NidApp app, string name = "Locatif", string startsOn = "2025-09-01",
        string? endsOn = null, int quarter = 2, string? dpe = null, PersonId? actor = null, string visibility = "shared")
    {
        var who = actor ?? app.AdultA;
        var body = Home(app, name, visibility, who);
        if (dpe is not null) body["energyClass"] = dpe;
        var property = await NewPropertyAsync(app, body, who);
        var tenancy = await CreatedAsync(await app.RawAsync(who, HttpMethod.Post, $"{Properties}/{property}/tenancies", new
        {
            label = "T2", startsOn, endsOn, rentMonthly = "800", chargesMonthly = "50", deposit = "800", note = "Bail Dupont",
            indexed = true, irlReferenceQuarter = quarter,
        }));
        return (property, tenancy);
    }

    private static string Revision(Guid property, Guid tenancy) => $"{Properties}/{property}/tenancies/{tenancy}/revision";

    private static async Task<JsonElement> GetRevisionAsync(NidApp app, Guid property, Guid tenancy)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, Revision(property, tenancy));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static Task<HttpResponseMessage> AcceptAsync(NidApp app, Guid property, Guid tenancy, string effectiveOn)
        => app.RawAsync(app.AdultA, HttpMethod.Post, Revision(property, tenancy), new { effectiveOn });

    private static List<JsonElement> Tenancies(JsonElement detail) => detail.GetProperty("tenancies").EnumerateArray().OrderBy(t => S(t, "startsOn")).ToList();

    [Fact]
    public async Task A_due_revision_is_accepted_on_the_anniversary_and_continues_the_tenancy()
    {
        await using var app = await StartAsync(fixture);
        var (property, old) = await LetAsync(app);

        var due = await GetRevisionAsync(app, property, old);
        Assert.Equal(("due", "2026-09-01", "2025-Q2", "146.68", "2026-Q2", "148.37"),
            (S(due, "state"), S(due, "anniversary"), S(due, "fromQuarter"), S(due, "fromValue"), S(due, "toQuarter"), S(due, "toValue")));
        Assert.Equal(("800.00", "809.22", "2026-10-15"), (S(due, "currentRent"), S(due, "newRent"), S(due, "defaultEffectiveOn")));
        Assert.True(due.GetProperty("dpeUnknown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, due.GetProperty("lostAnniversary").ValueKind);

        var revised = await CreatedAsync(await AcceptAsync(app, property, old, "2026-09-01"));

        var tenancies = Tenancies(await PropertyAsync(app, property));
        Assert.Equal(2, tenancies.Count);
        Assert.Equal((old, "2026-09-01"), (tenancies[0].GetProperty("id").GetGuid(), S(tenancies[0], "endsOn")));
        var next = tenancies[1];
        Assert.Equal((revised, old), (next.GetProperty("id").GetGuid(), next.GetProperty("revisedFrom").GetGuid()));
        Assert.Equal(("2026-09-01", "809.22", "50.00", "800.00", "T2", "Bail Dupont"),
            (S(next, "startsOn"), S(next, "rentMonthly"), S(next, "chargesMonthly"), S(next, "deposit"), S(next, "label"), S(next, "note")));
        Assert.Equal(JsonValueKind.Null, next.GetProperty("endsOn").ValueKind);
        Assert.Equal((true, 2), (next.GetProperty("indexed").GetBoolean(), next.GetProperty("irlReferenceQuarter").GetInt32()));
        // The chain's last link carries its revision state; the history does not.
        Assert.Equal(JsonValueKind.Null, tenancies[0].GetProperty("revision").ValueKind);
        Assert.Equal("none", S(next.GetProperty("revision"), "state"));

        Assert.Equal("none", S(await GetRevisionAsync(app, property, revised), "state"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, NotDue, (string?)null), await RefusalAsync(await AcceptAsync(app, property, revised, "2026-10-01")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, NotDue, (string?)null), await RefusalAsync(await AcceptAsync(app, property, old, "2026-10-01")));
    }

    [Fact]
    public async Task A_revision_copies_the_tenant_and_landlord_identity() // Rental receipts spec §2
    {
        await using var app = await StartAsync(fixture);
        var (property, old) = await LetAsync(app);
        var url = $"{Properties}/{property}/tenancies/{old}";
        var version = Tenancies(await PropertyAsync(app, property))[0].GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, url, new
        {
            label = "T2", startsOn = "2025-09-01", rentMonthly = "800", chargesMonthly = "50", deposit = "800", note = "Bail Dupont",
            tenantNames = "Mme Martin", landlordName = "Alice et Bob", landlordAddress = "1 rue A\n75001 Paris", rentsTrackedFrom = "2025-10", version,
        })).StatusCode);

        await CreatedAsync(await AcceptAsync(app, property, old, "2026-09-01"));

        var next = Tenancies(await PropertyAsync(app, property))[1];
        Assert.Equal(("Mme Martin", "Alice et Bob", "1 rue A\n75001 Paris", "2025-10"),
            (S(next, "tenantNames"), S(next, "landlordName"), S(next, "landlordAddress"), S(next, "rentsTrackedFrom")));
        // The copied month, before the new link's own start, is its chain's: a correction of the link keeps it.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{property}/tenancies/{next.GetProperty("id").GetGuid()}", new
        {
            label = "T2 révisé", startsOn = S(next, "startsOn"), rentMonthly = S(next, "rentMonthly"), chargesMonthly = "50", version = next.GetProperty("version").GetUInt32(),
        })).StatusCode);
    }

    [Fact]
    public async Task The_effective_date_lies_between_the_anniversary_and_today()
    {
        await using var app = await StartAsync(fixture);
        var (property, tenancy) = await LetAsync(app);

        foreach (var effectiveOn in new[] { "2026-08-31", "2026-10-16" })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, EffectiveOn, "effectiveOn"),
                await RefusalAsync(await AcceptAsync(app, property, tenancy, effectiveOn)));
        Assert.Single(Tenancies(await PropertyAsync(app, property)));
    }

    [Fact]
    public async Task A_mid_month_revision_prorates_both_tenancies_in_the_routine() // Review Focus #2
    {
        await using var app = await StartAsync(fixture);
        var (property, old) = await LetAsync(app);
        var revised = await CreatedAsync(await AcceptAsync(app, property, old, "2026-09-16"));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/realestate/routine?on=2026-09-30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rents = (await JsonAsync(response)).GetProperty("rents").EnumerateArray().Where(r => S(r, "category") == "rent").ToList();

        var september = new LocalDate(2026, 9, 1);
        var before = PropertyMath.RentIn(new Tenancy("T2", new LocalDate(2025, 9, 1), new LocalDate(2026, 9, 16), 800m, 50m, null, null), september, charges: false)!.Value;
        var after = PropertyMath.RentIn(new Tenancy("T2", new LocalDate(2026, 9, 16), null, 809.22m, 50m, null, null), september, charges: false)!.Value;
        // 15 days of 30 on each side: no day uncovered, none counted twice.
        Assert.Equal((400.00m, 404.61m), (before.Amount, after.Amount));
        Assert.Equal([(old, "400.00", "2026-09-01"), (revised, "404.61", "2026-09-16")],
            rents.Select(r => (r.GetProperty("tenancyId").GetGuid(), S(r, "amount"), S(r, "occurredOn"))));
    }

    [Fact]
    public async Task A_property_in_F_or_G_is_never_revised()
    {
        await using var app = await StartAsync(fixture);
        var (property, tenancy) = await LetAsync(app, dpe: "G");

        var state = await GetRevisionAsync(app, property, tenancy);
        Assert.Equal(("forbidden_dpe", "2026-09-01"), (S(state, "state"), S(state, "anniversary")));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("newRent").ValueKind);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, ForbiddenDpe, (string?)null), await RefusalAsync(await AcceptAsync(app, property, tenancy, "2026-09-01")));
    }

    [Fact]
    public async Task Undo_restores_the_end_and_is_refused_once_a_rent_is_recorded_from_the_effective_date() // Review Focus #3
    {
        await using var app = await StartAsync(fixture);
        var (property, old) = await LetAsync(app, endsOn: "2028-09-01");
        Task<HttpResponseMessage> Undo(Guid tenancy) => app.RawAsync(app.AdultA, HttpMethod.Delete, Revision(property, tenancy));
        async Task RentAsync(string occurredOn) => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/flows",
            new { occurredOn, kind = "income", amount = "800", category = "rent" }));

        // Only a revision is undone.
        Assert.Equal((HttpStatusCode.UnprocessableEntity, UndoRefused, (string?)null), await RefusalAsync(await Undo(old)));

        var revised = await CreatedAsync(await AcceptAsync(app, property, old, "2026-09-16"));
        await RentAsync("2026-09-05"); // before the effective date: the old tenancy's
        Assert.Equal(HttpStatusCode.NoContent, (await Undo(revised)).StatusCode);
        var only = Assert.Single(Tenancies(await PropertyAsync(app, property)));
        Assert.Equal((old, "2028-09-01"), (only.GetProperty("id").GetGuid(), S(only, "endsOn")));
        Assert.Equal("due", S(await GetRevisionAsync(app, property, old), "state"));

        revised = await CreatedAsync(await AcceptAsync(app, property, old, "2026-09-16"));
        await RentAsync("2026-10-05");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, UndoRefused, (string?)null), await RefusalAsync(await Undo(revised)));
        Assert.Equal(2, Tenancies(await PropertyAsync(app, property)).Count);
    }

    [Fact]
    public async Task The_overview_names_the_four_findings_and_none_of_a_hidden_property() // Review Focus #5
    {
        await using var app = await StartAsync(fixture);
        var (due, _) = await LetAsync(app, "Due");
        var (lost, _) = await LetAsync(app, "Perdu", startsOn: "2024-09-01");
        var (missing, _) = await LetAsync(app, "Manque", quarter: 3);
        var (forbidden, _) = await LetAsync(app, "Passoire", dpe: "F");
        var (hidden, _) = await LetAsync(app, "Privé de B", actor: app.AdultB, visibility: "private");

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/overview?scope=household");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var watch = (await JsonAsync(response)).GetProperty("watch").EnumerateArray()
            .Where(w => S(w, "code")!.StartsWith("revision_") || S(w, "code") == "irl_missing")
            .Select(w => (S(w, "code"), w.GetProperty("propertyId").GetGuid(), S(w, "on"),
                w.GetProperty("amount").ValueKind == JsonValueKind.Null ? null : S(w.GetProperty("amount"), "amount")))
            .OrderBy(w => w.Item1).ThenBy(w => w.Item3).ToList();

        Assert.Equal([
            ("irl_missing", missing, "2026-09-01", null),
            ("revision_due", due, "2026-09-01", "809.22"),
            ("revision_due", lost, "2026-09-01", "809.22"),
            ("revision_forbidden_dpe", forbidden, "2026-09-01", null),
            ("revision_lost", lost, "2025-09-01", null),
        ], watch.OrderBy(w => w.Item1).ThenBy(w => w.Item2 == due ? 0 : 1).ToList());
        Assert.DoesNotContain(hidden.ToString(), (await app.GetRawAsync(app.AdultA, $"{Properties}/overview?scope=household")));
    }

    [Fact]
    public async Task A_revision_on_the_day_is_no_tenancy_end_in_the_overview_nor_the_dashboard()
    {
        await using var app = await StartAsync(fixture);
        var (property, old) = await LetAsync(app);
        await CreatedAsync(await AcceptAsync(app, property, old, "2026-10-15")); // the old link now ends today

        var watch = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/overview?scope=household"))).GetProperty("watch");
        var upcoming = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement.GetProperty("upcoming");

        Assert.DoesNotContain(watch.EnumerateArray(), w => S(w, "code") == "tenancy_end");
        Assert.DoesNotContain(upcoming.EnumerateArray(), e => S(e, "kind") == "tenancy_end");
    }

    [Fact]
    public async Task A_revised_link_keeps_its_start()
    {
        await using var app = await StartAsync(fixture);
        var (property, old) = await LetAsync(app);
        var revised = await CreatedAsync(await AcceptAsync(app, property, old, "2026-09-01"));
        var link = Tenancies(await PropertyAsync(app, property)).Single(t => t.GetProperty("id").GetGuid() == revised);
        var body = new
        {
            label = "T2", startsOn = "2026-09-02", rentMonthly = "809.22", chargesMonthly = "50", deposit = "800", note = "Bail Dupont",
            indexed = true, irlReferenceQuarter = 2, version = link.GetProperty("version").GetInt32(),
        };

        var moved = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{property}/tenancies/{revised}", body);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.tenancy_revised", "startsOn"), await RefusalAsync(moved));
        var kept = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{property}/tenancies/{revised}", body with { startsOn = "2026-09-01", note = "Bail Dupont, révisé" });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    private static async Task<McpClient> ClientAsync(NidApp app, string preset)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    [Fact]
    public async Task The_assistant_lists_the_due_revisions_and_proposes_one()
    {
        await using var app = await StartAsync(fixture);
        var (property, tenancy) = await LetAsync(app);
        await LetAsync(app, "Privé de B", actor: app.AdultB, visibility: "private");

        await using (var read = await ClientAsync(app, TokenPresets.Read))
        {
            var (error, text) = await CallAsync(read, "get_rent_revisions", new { scope = "household" });
            Assert.False(error, text);
            var item = Assert.Single(JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal((property, "Locatif", tenancy), (item.GetProperty("propertyId").GetGuid(), S(item, "propertyName"), item.GetProperty("tenancyId").GetGuid()));
            Assert.Equal(("2026-09-01", "2025-Q2", "146.68", "2026-Q2", "148.37", "800.00", "809.22", "2026-10-15", "EUR"),
                (S(item, "anniversary"), S(item, "fromQuarter"), S(item, "fromValue"), S(item, "toQuarter"), S(item, "toValue"),
                    S(item, "currentRent"), S(item, "newRent"), S(item, "defaultEffectiveOn"), S(item, "currency")));
        }

        await using (var propose = await ClientAsync(app, TokenPresets.Propose))
        {
            var (error, text) = await CallAsync(propose, "propose_rent_revision", new { id = property, tenancyId = tenancy, body = new { effectiveOn = "2026-08-31" } });
            Assert.True(error);
            Assert.Contains("effectiveOn", text);

            (error, text) = await CallAsync(propose, "propose_rent_revision", new { id = property, tenancyId = tenancy, body = new { effectiveOn = "2026-09-01" } });
            Assert.False(error, text);
            Assert.Equal("proposed", JsonDocument.Parse(text).RootElement.GetProperty("outcome").GetString());
            Assert.Single(Tenancies(await PropertyAsync(app, property))); // the dry run wrote nothing
        }

        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        var (failed, applied) = await CallAsync(direct, "propose_rent_revision", new { id = property, tenancyId = tenancy, body = new { effectiveOn = "2026-09-01" }, apply = true });
        Assert.False(failed, applied);
        Assert.Equal("applied", JsonDocument.Parse(applied).RootElement.GetProperty("outcome").GetString());
        Assert.Equal("809.22", S(Tenancies(await PropertyAsync(app, property))[1], "rentMonthly"));
    }
}
