using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Childcare.IntegrationTests;

/// <summary>Childcare spec §1.8–11, §5: the arrangement's reads and writes. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class ChildcareApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/childcare";

    private static object[] Schedule(string departure = "17:30")
        => [.. new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure, lunch = true })];

    private static Dictionary<string, object?> Terms(string from = "2025-09-01", string rate = "4.30", object[]? schedule = null, int weeks = 46) => new()
    {
        ["from"] = from, ["netHourlyRate"] = rate, ["grossHourlyRate"] = null, ["overtimePct"] = "10", ["upkeepPer9h"] = "3.80",
        ["lunchPrice"] = "3.20", ["weekTypes"] = new[] { new { name = "Semaine type", weeks, schedule = schedule ?? Schedule() } },
    };

    private static Dictionary<string, object?> Body(NidApp app, PersonId? child = null, object? terms = null, object[]? gaps = null,
        string visibility = "shared") => new()
    {
        ["name"] = "Garde de Léa", ["childId"] = (child ?? app.Dependent).Value,
        ["carer"] = new { name = "Martine", pajemploi = "P123", approval = (string?)null },
        ["terms"] = terms ?? Terms(), ["gaps"] = gaps ?? [], ["visibility"] = visibility,
        ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<ItemId> CreateAsync(NidApp app, Dictionary<string, object?>? body = null)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, body ?? Body(app));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new ItemId((await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId actor, string url)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task AssertFieldAsync(HttpResponseMessage response, params (string Path, string Code)[] expected)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await JsonAsync(response)).GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("path").GetString()!, e.GetProperty("code").GetString()!)).ToList();
        foreach (var e in expected) Assert.Contains(e, errors);
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_and_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var detail = await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household");
        var summary = detail.GetProperty("summary");
        Assert.Equal("626.37", summary.GetProperty("monthlyBasePay").GetProperty("amount").GetString());
        Assert.Equal("38.00", summary.GetProperty("weeklyHours").GetString());
        Assert.Equal("Martine", summary.GetProperty("carerName").GetString());
        Assert.True(summary.GetProperty("canWrite").GetBoolean());
        var terms = Assert.Single(detail.GetProperty("terms").EnumerateArray());
        Assert.Equal(146, terms.GetProperty("normalHours").GetInt32());
        Assert.Equal("08:00", terms.GetProperty("weekTypes")[0].GetProperty("schedule")[0].GetProperty("arrival").GetString());
        Assert.Equal(6, detail.GetProperty("gapWeeksToPlace").GetInt32());
        Assert.Single((await GetAsync(app, app.AdultA, $"{Url}?scope=household")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Adult_as_child_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, child: app.AdultB)), ("childId", "childcare.child_not_dependent"));
    }

    [Fact]
    public async Task Bad_schedule_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        object[] schedule = [new { day = 1, arrival = "08:00", departure = "07:00", lunch = false }, new { day = 1, arrival = "08:00", departure = "17:00", lunch = false }];
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, terms: Terms(schedule: schedule))),
            ("terms.weekTypes[0].schedule[1].day", "childcare.schedule_duplicate_day"), ("terms.weekTypes[0].schedule[0].departure", "childcare.departure_before_arrival"));
    }

    [Fact]
    public async Task Too_many_gaps_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        object[] gaps = [new { monday = "2026-02-16", weeks = 7 }];
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, gaps: gaps)), ("gaps", "childcare.gaps_too_many"));
    }

    [Fact]
    public async Task Revise_closes_the_previous_terms()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var revised = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/revisions", new { terms = Terms("2026-01-01", "4.50") });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var detail = await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household");
        var terms = detail.GetProperty("terms").EnumerateArray().ToList();
        Assert.Equal(2, terms.Count);
        Assert.Equal("2025-12-31", terms[0].GetProperty("to").GetString());
        Assert.Equal("655.50", terms[1].GetProperty("monthlyBasePay").GetProperty("amount").GetString());
        // The revision starts after today: the summary keeps the terms in force.
        Assert.Equal("626.37", detail.GetProperty("summary").GetProperty("monthlyBasePay").GetProperty("amount").GetString());
        Assert.Equal("2025-09-01", detail.GetProperty("summary").GetProperty("inForceFrom").GetString());
    }

    [Fact]
    public async Task Revision_not_after_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/revisions", new { terms = Terms("2025-09-01") }),
            ("terms.from", "childcare.revision_not_after"));
    }

    /// <summary>#192: a past revision's absences stay correctable; the later one keeps its own.</summary>
    [Fact]
    public async Task A_past_revision_takes_new_unpaid_absences()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/revisions", new { terms = Terms("2025-10-01") })).StatusCode);
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/revisions/2025-09-01", new { unpaidAbsences = new[] { "family", "child_sick" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var terms = (await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household")).GetProperty("terms").EnumerateArray()
            .Select(t => t.GetProperty("unpaidAbsences").EnumerateArray().Select(a => a.GetString()).ToList()).ToList();
        Assert.Equal(["family", "child_sick"], terms[0]);
        Assert.Equal(["parent_rtt", "child_sick", "carer_absent"], terms[1]);
    }

    /// <summary>A past revision is correctable in full: its start and end stay, the later revision keeps its own terms.</summary>
    [Fact]
    public async Task A_past_revision_is_edited_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/revisions", new { terms = Terms("2025-10-01") })).StatusCode);
        var version = (await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household")).GetProperty("version").GetUInt32();
        object Edit(string revision) => new { name = "Garde de Léa", carer = new { name = "Martine" }, terms = Terms("2025-09-01", "5.00", weeks: 40), version, revision };
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}", Edit("2025-09-02"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}", Edit("2025-09-01"))).StatusCode);
        var terms = (await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household")).GetProperty("terms").EnumerateArray().ToList();
        Assert.Equal(2, terms.Count);
        Assert.Equal(("2025-09-01", "2025-09-30", 5.00m), (terms[0].GetProperty("from").GetString(), terms[0].GetProperty("to").GetString(), decimal.Parse(terms[0].GetProperty("netHourlyRate").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(40, terms[0].GetProperty("weekTypes")[0].GetProperty("weeks").GetInt32());
        Assert.Equal(4.30m, decimal.Parse(terms[1].GetProperty("netHourlyRate").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Unpaid_absences_of_an_unknown_revision_or_reason_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var url = $"{Url}/{id.Value}/revisions";
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/2025-09-02", new { unpaidAbsences = Array.Empty<string>() })).StatusCode);
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/2025-09-01", new { unpaidAbsences = new[] { "nap" } }), ("unpaidAbsences", "request.invalid"));
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/2025-09-01", new { }), ("unpaidAbsences", "request.required"));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/close", new { on = "2025-10-31" })).StatusCode);
        await AssertCodeAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/2025-09-01", new { unpaidAbsences = Array.Empty<string>() }),
            HttpStatusCode.Conflict, "childcare.closed");
    }

    [Fact]
    public async Task Stale_version_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var version = (await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household")).GetProperty("version").GetUInt32();
        object Edit(string name) => new { name, carer = new { name = "Martine" }, terms = Terms(), version };
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}", Edit("Garde"))).StatusCode);
        await AssertCodeAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}", Edit("Autre")), HttpStatusCode.Conflict, "childcare.conflict");
    }

    [Fact]
    public async Task Edit_read_before_a_revision_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var version = (await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household")).GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/revisions", new { terms = Terms("2026-01-01", "4.50") })).StatusCode);
        await AssertCodeAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}", new { name = "Garde", carer = new { name = "Martine" }, terms = Terms(), version }),
            HttpStatusCode.Conflict, "childcare.conflict");
    }

    [Fact]
    public async Task Replace_gaps_counts_what_is_left()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/gaps", new { gaps = new[] { new { monday = "2026-02-16", weeks = 2 } } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var detail = await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household");
        Assert.Equal("2026-03-01", detail.GetProperty("gaps")[0].GetProperty("sunday").GetString());
        Assert.Equal(4, detail.GetProperty("gapWeeksToPlace").GetInt32());
    }

    [Fact]
    public async Task Close_then_reopen()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/close", new { on = "2025-10-31" })).StatusCode);
        Assert.Empty((await GetAsync(app, app.AdultA, $"{Url}?scope=household")).GetProperty("items").EnumerateArray());
        var closed = Assert.Single((await GetAsync(app, app.AdultA, $"{Url}?scope=household&closed=true")).GetProperty("items").EnumerateArray());
        Assert.Equal("2025-10-31", closed.GetProperty("end").GetString());
        Assert.True(closed.GetProperty("closed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/{id.Value}/reopen")).StatusCode);
        var reopened = (await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household")).GetProperty("summary");
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("end").ValueKind);
    }

    [Fact]
    public async Task Shared_reader_cannot_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var summary = (await GetAsync(app, app.AdultB, $"{Url}/{id.Value}?scope=household")).GetProperty("summary");
        Assert.False(summary.GetProperty("canWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Url}/{id.Value}/close", new { on = "2025-10-31" })).StatusCode);
    }

    [Fact]
    public async Task Me_scope_hides_the_other_adults_private_arrangement()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CreateAsync(app, Body(app, visibility: "private"));
        Assert.Empty((await GetAsync(app, app.AdultB, $"{Url}?scope=household")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Missing_week_type_fields_are_required()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var terms = Terms();
        terms["weekTypes"] = new object[] { new { schedule = Schedule() } };
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, terms: terms)),
            ("terms.weekTypes[0].name", "request.required"), ("terms.weekTypes[0].weeks", "request.required"));
        terms["weekTypes"] = null;
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, terms: terms)), ("terms.weekTypes", "request.required"));
        terms["weekTypes"] = Array.Empty<object>();
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, terms: terms)), ("terms.weekTypes", "childcare.week_types_empty"));
    }

    private static Dictionary<string, object?> TermsOf(params object[] types)
    {
        var terms = Terms();
        terms["weekTypes"] = types;
        return terms;
    }

    [Fact]
    public async Task A_week_type_of_no_week_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var terms = TermsOf(new { name = "Longue", weeks = 23, schedule = Schedule() }, new { name = "Courte", weeks = 0, schedule = Schedule() });
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, terms: terms)),
            ("terms.weekTypes[1].weeks", "childcare.week_type_weeks_out_of_range"));
    }

    [Fact]
    public async Task A_blank_or_repeated_week_type_name_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url,
                Body(app, terms: TermsOf(new { name = "  ", weeks = 46, schedule = Schedule() }))),
            ("terms.weekTypes[0].name", "childcare.week_type_name_invalid"));
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url,
                Body(app, terms: TermsOf(new { name = "Longue", weeks = 23, schedule = Schedule() }, new { name = " longue ", weeks = 22, schedule = Schedule() }))),
            ("terms.weekTypes[1].name", "childcare.week_type_name_invalid"));
    }

    [Fact]
    public async Task Week_types_beyond_46_weeks_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var terms = TermsOf(new { name = "Longue", weeks = 30, schedule = Schedule() }, new { name = "Courte", weeks = 17, schedule = Schedule() });
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, terms: terms)),
            ("terms.weekTypes", "childcare.programmed_weeks_out_of_range"));
    }

    [Fact]
    public async Task Two_week_types_are_created()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var terms = Terms();
        terms["weekTypes"] = new object[] { new { name = "Longue", weeks = 23, schedule = Schedule() }, new { name = "Courte", weeks = 22, schedule = Schedule("16:00") } };
        var id = await CreateAsync(app, Body(app, terms: terms));
        var detail = await GetAsync(app, app.AdultA, $"{Url}/{id.Value}?scope=household");
        Assert.Equal(2, detail.GetProperty("terms")[0].GetProperty("weekTypes").GetArrayLength());
    }

    [Fact]
    public async Task Week_override_is_set_then_refused_on_a_tuesday()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var terms = Terms();
        terms["weekTypes"] = new object[] { new { name = "Longue", weeks = 23, schedule = Schedule() }, new { name = "Courte", weeks = 22, schedule = Schedule("16:00") } };
        var id = await CreateAsync(app, Body(app, terms: terms));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/week-overrides/2025-09-08", new { weekType = 1 })).StatusCode);
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/week-overrides/2025-09-09", new { weekType = 1 }),
            ("monday", "childcare.override_invalid_monday"));
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/week-overrides/2025-09-08", new { weekType = 9 }),
            ("weekType", "childcare.override_unknown_week_type"));
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/week-overrides/2025-09-08", new { }), ("weekType", "request.required"));
        await AssertFieldAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id.Value}/week-overrides/demain", new { weekType = 1 }), ("monday", "request.date"));
    }
}
