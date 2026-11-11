using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Childcare.IntegrationTests;

/// <summary>Childcare spec §5 « Relevé du mois », decisions 12–17. Today is 2025-10-15; the arrangement starts on 2025-09-01 (4,30 € net, Mon/Tue/Thu/Fri 08:00–17:30, 46 weeks).</summary>
public class ChildcareStatementTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/childcare";

    private static object Body(NidApp app) => new
    {
        name = "Garde de Léa", childId = app.Dependent.Value,
        carer = new { name = "Martine", pajemploi = "P123", approval = (string?)null },
        terms = new
        {
            from = "2025-09-01", netHourlyRate = "4.30", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = "3.20",
            weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "17:30", lunch = true }).ToArray() } },
        },
        gaps = Array.Empty<object>(), visibility = "shared",
        owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreateAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return $"{Url}/{(await JsonAsync(response)).GetProperty("id").GetGuid()}";
    }

    private static async Task<JsonElement> GetAsync(NidApp app, string url)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static Dictionary<string, object?> Day(JsonElement d) => new()
    {
        ["date"] = d.GetProperty("date").GetString(), ["present"] = d.GetProperty("present").GetBoolean(),
        ["arrival"] = d.GetProperty("arrival").GetString(), ["departure"] = d.GetProperty("departure").GetString(),
        ["lunch"] = d.GetProperty("lunch").GetBoolean(), ["absence"] = d.GetProperty("absence").GetString(),
    };

    /// <summary>The month as read, with <paramref name="edits"/> by date, saved with the version read.</summary>
    private static async Task<HttpResponseMessage> SaveAsync(NidApp app, string url, Dictionary<string, Action<Dictionary<string, object?>>>? edits = null,
        string mileage = "0", string? cmg = null, PersonId? actor = null)
    {
        var month = await GetAsync(app, url);
        var days = month.GetProperty("days").EnumerateArray().Select(Day).ToList();
        foreach (var day in days)
            if (edits is not null && edits.TryGetValue((string)day["date"]!, out var edit)) edit(day);
        return await app.RawAsync(actor ?? app.AdultA, HttpMethod.Put, url,
            new { days, mileage, cmg, version = month.GetProperty("version").GetUInt32() });
    }

    private static async Task<HttpResponseMessage> ValidateAsync(NidApp app, string url)
        => await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/validate", new { version = (await GetAsync(app, url)).GetProperty("version").GetUInt32() });

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static readonly Action<Dictionary<string, object?>> SickChild = d =>
    {
        d["present"] = false; d["arrival"] = null; d["departure"] = null; d["lunch"] = false; d["absence"] = "child_sick";
    };

    [Fact]
    public async Task Months_list_from_start_to_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        var months = (await GetAsync(app, $"{url}/statements")).GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(["2025-10", "2025-09"], months.Select(m => m.GetProperty("month").GetString()));
        Assert.All(months, m => Assert.Equal("none", m.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Prepared_month_reads_from_the_template()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        var month = await GetAsync(app, $"{url}/statements/2025-09");
        Assert.Equal(30, month.GetProperty("days").GetArrayLength());
        Assert.Equal(18, month.GetProperty("days").EnumerateArray().Count(d => d.GetProperty("planned").GetBoolean()));
        var figures = month.GetProperty("figures");
        var declaration = figures.GetProperty("declaration");
        Assert.Equal("626.37", declaration.GetProperty("netPay").GetString());
        Assert.Equal(146, declaration.GetProperty("normalHours").GetInt32());
        Assert.Equal(16, declaration.GetProperty("activityDays").GetInt32());
        Assert.True(figures.GetProperty("costBeforeCmg").GetBoolean());
        Assert.Equal(0u, month.GetProperty("version").GetUInt32());
        Assert.Equal("none", month.GetProperty("state").GetString());
    }

    /// <summary>#192: correcting a past revision's absences recomputes its draft months.</summary>
    [Fact]
    public async Task A_draft_month_follows_its_revision_s_corrected_absences()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/revisions", new
        {
            terms = new
            {
                from = "2025-10-01", netHourlyRate = "4.30", overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = "3.20",
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "17:30", lunch = true }).ToArray() } },
            },
        })).StatusCode);
        var url = $"{arrangement}/statements/2025-09";
        Action<Dictionary<string, object?>> family = d => { SickChild(d); d["absence"] = "family"; };
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(app, url, new() { ["2025-09-01"] = family })).StatusCode);
        Assert.Equal("0.00", (await GetAsync(app, url)).GetProperty("figures").GetProperty("deduction").GetProperty("amount").GetString());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{arrangement}/revisions/2025-09-01", new { unpaidAbsences = new[] { "family" } })).StatusCode);
        Assert.NotEqual("0.00", (await GetAsync(app, url)).GetProperty("figures").GetProperty("deduction").GetProperty("amount").GetString());
    }

    /// <summary>Decisions 29 and 30: a past month from its totals and Pajemploi's amounts, kept through validation.</summary>
    [Fact]
    public async Task A_past_month_from_its_totals()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, url, new
        {
            days = Array.Empty<object>(), mileage = "0", cmg = "10", version = 0u,
            declared = new { netPay = "600.00", upkeep = "50", meals = "40", normalHours = 120, activityDays = 11 },
            summary = new { careDays = 10, hours = "85.5", absence = "family" },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var month = await JsonAsync(saved);
        Assert.Equal(10, month.GetProperty("days").EnumerateArray().Count(d => d.GetProperty("present").GetBoolean()));
        Assert.Contains(month.GetProperty("days").EnumerateArray(), d => d.GetProperty("absence").GetString() == "family");
        // #32: Nid's own figures stay beside the ones entered by hand.
        Assert.Equal(120, month.GetProperty("figures").GetProperty("declaration").GetProperty("normalHours").GetInt32());
        Assert.Equal(146, month.GetProperty("computed").GetProperty("normalHours").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(app, url)).StatusCode);
        var frozen = await GetAsync(app, url);
        var declaration = frozen.GetProperty("figures").GetProperty("declaration");
        Assert.Equal("600.00", declaration.GetProperty("netPay").GetString());
        Assert.Equal("50.00", declaration.GetProperty("upkeep").GetString());
        Assert.Equal("40.00", declaration.GetProperty("meals").GetString());
        Assert.Equal(120, declaration.GetProperty("normalHours").GetInt32());
        Assert.Equal(11, declaration.GetProperty("activityDays").GetInt32());
        Assert.Equal("600.00", frozen.GetProperty("declared").GetProperty("netPay").GetString());
        Assert.Equal(146, frozen.GetProperty("computed").GetProperty("normalHours").GetInt32());
    }

    /// <summary>#32: clearing the fields entered by hand brings the month back to Nid's figures.</summary>
    [Fact]
    public async Task Clearing_the_declared_figures_brings_back_Nids()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        var days = (await GetAsync(app, url)).GetProperty("days").EnumerateArray().Select(Day).ToList();
        var saved = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { days, mileage = "0", version = 0u, declared = new { normalHours = 120 } }));
        Assert.Equal(120, saved.GetProperty("figures").GetProperty("declaration").GetProperty("normalHours").GetInt32());
        var cleared = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { days, mileage = "0", version = saved.GetProperty("version").GetUInt32() }));
        Assert.Equal(146, cleared.GetProperty("figures").GetProperty("declaration").GetProperty("normalHours").GetInt32());
        Assert.Equal(cleared.GetProperty("figures").GetProperty("declaration").ToString(), cleared.GetProperty("computed").ToString());
    }

    [Fact]
    public async Task A_summary_with_more_days_than_the_month_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Put, url, new
        {
            days = Array.Empty<object>(), mileage = "0", version = 0u, summary = new { careDays = 40, hours = "300" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("childcare.summary_too_many_days", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Correcting_two_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        var saved = await SaveAsync(app, url, new()
        {
            ["2025-09-08"] = SickChild,
            ["2025-09-09"] = d => d["departure"] = "19:00",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var month = await JsonAsync(saved);
        var declaration = month.GetProperty("figures").GetProperty("declaration");
        Assert.Equal("598.02", declaration.GetProperty("netPay").GetString());
        Assert.Equal("2.00", declaration.GetProperty("complementaryHours").GetString());
        Assert.Equal(15, declaration.GetProperty("activityDays").GetInt32());
        Assert.Equal("draft", month.GetProperty("state").GetString());
        Assert.Equal(["2025-09-08", "2025-09-09"], month.GetProperty("days").EnumerateArray()
            .Where(d => d.GetProperty("changed").GetBoolean()).Select(d => d.GetProperty("date").GetString()));
    }

    [Fact]
    public async Task Validate_needs_the_cmg()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var url = $"{arrangement}/statements/2025-09";
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(app, url)).StatusCode);
        var refused = await ValidateAsync(app, url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains((await JsonAsync(refused)).GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("path").GetString() == "cmg" && e.GetProperty("code").GetString() == "childcare.cmg_required");

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(app, url, cmg: "100")).StatusCode);
        var validated = await ValidateAsync(app, url);
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.Equal("validated", (await JsonAsync(validated)).GetProperty("state").GetString());
        var row = (await GetAsync(app, $"{arrangement}/statements")).GetProperty("months").EnumerateArray().Single(m => m.GetProperty("month").GetString() == "2025-09");
        Assert.Equal("validated", row.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("cost").GetProperty("amount").GetString()));
    }

    [Fact]
    public async Task Validated_month_is_read_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        await SaveAsync(app, url, cmg: "0");
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(app, url)).StatusCode);
        await AssertCodeAsync(await SaveAsync(app, url, cmg: "0"), HttpStatusCode.Conflict, "childcare.statement_validated");
    }

    [Fact]
    public async Task Reopen_refused_when_next_is_validated()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        foreach (var m in new[] { "2025-09", "2025-10" })
        {
            await SaveAsync(app, $"{arrangement}/statements/{m}", cmg: "0");
            Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(app, $"{arrangement}/statements/{m}")).StatusCode);
        }
        Assert.False((await GetAsync(app, $"{arrangement}/statements/2025-09")).GetProperty("canReopen").GetBoolean());
        await AssertCodeAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/statements/2025-09/reopen"),
            HttpStatusCode.Conflict, "childcare.next_validated");
        var reopened = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/statements/2025-10/reopen");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal("draft", (await JsonAsync(reopened)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Stale_draft_after_a_gap_change_is_flagged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        await SaveAsync(app, $"{arrangement}/statements/2025-09", cmg: "0");
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(app, $"{arrangement}/statements/2025-09")).StatusCode);
        var url = $"{arrangement}/statements/2025-10";
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(app, url, new()
        {
            ["2025-10-06"] = d => { SickChild(d); d["absence"] = "family"; },
        }, cmg: "0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{arrangement}/gaps",
            new { gaps = new[] { new { monday = "2025-10-06", weeks = 1 } } })).StatusCode);

        var month = await GetAsync(app, url);
        Assert.Equal(JsonValueKind.Null, month.GetProperty("figures").ValueKind);
        var problems = month.GetProperty("problems").EnumerateArray().Select(p => (p.GetProperty("field").GetString(), p.GetProperty("code").GetString())).ToList();
        Assert.Contains(("days[5].absence", "childcare.absence_not_allowed"), problems);
        Assert.Contains(("days[5].planned", "childcare.day_replanned"), problems); // the correction of Monday 6 October, now in the gap
        Assert.False(month.GetProperty("days")[6].GetProperty("present").GetBoolean()); // Tuesday 7 October, untouched, follows the gap
        await AssertCodeAsync(await ValidateAsync(app, url), HttpStatusCode.Conflict, "childcare.statement_incomplete");

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, url)).StatusCode);
        month = await GetAsync(app, url);
        Assert.Equal("none", month.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Object, month.GetProperty("figures").ValueKind);
    }

    [Fact]
    public async Task Untouched_replanned_days_take_the_template_on_save()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var url = $"{arrangement}/statements/2025-10";
        await SaveAsync(app, url, cmg: "0");
        await app.RawAsync(app.AdultA, HttpMethod.Put, $"{arrangement}/gaps", new { gaps = new[] { new { monday = "2025-10-06", weeks = 1 } } });
        var saved = await JsonAsync(await SaveAsync(app, url, cmg: "0"));
        Assert.Empty(saved.GetProperty("problems").EnumerateArray());
        Assert.False(saved.GetProperty("days")[6].GetProperty("present").GetBoolean());
        Assert.Equal("0.00", saved.GetProperty("figures").GetProperty("declaration").GetProperty("complementaryHours").GetString());
    }

    [Fact]
    public async Task Month_outside_the_contract_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        foreach (var m in new[] { "2025-08", "2025-11" })
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{arrangement}/statements/{m}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{arrangement}/statements/2025-13")).StatusCode);
    }

    [Fact]
    public async Task Bad_day_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        var response = await SaveAsync(app, url, new() { ["2025-09-02"] = d => d["departure"] = "07:00" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains((await JsonAsync(response)).GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("path").GetString() == "days[1].departure");
    }

    [Fact]
    public async Task Shared_reader_cannot_save()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"{await CreateAsync(app)}/statements/2025-09";
        var month = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, url));
        Assert.False(month.GetProperty("canWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(app, url, actor: app.AdultB)).StatusCode);
    }

    [Fact]
    public async Task Validate_needs_the_previous_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        await SaveAsync(app, $"{arrangement}/statements/2025-10", cmg: "0");
        await AssertCodeAsync(await ValidateAsync(app, $"{arrangement}/statements/2025-10"), HttpStatusCode.Conflict, "childcare.previous_not_validated");
    }

    [Fact]
    public async Task Close_before_a_validated_month_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        foreach (var m in new[] { "2025-09", "2025-10" })
        {
            await SaveAsync(app, $"{arrangement}/statements/{m}", cmg: "0");
            Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(app, $"{arrangement}/statements/{m}")).StatusCode);
        }
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/close", new { on = "2025-09-20" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains((await JsonAsync(refused)).GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("path").GetString() == "on" && e.GetProperty("code").GetString() == "childcare.end_before_validated");
    }

    [Fact]
    public async Task Template_correction_moves_untouched_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var url = $"{arrangement}/statements/2025-10";
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(app, url, new() { ["2025-10-02"] = d => d["departure"] = "19:00" })).StatusCode);
        var version = (await GetAsync(app, arrangement)).GetProperty("version").GetUInt32();
        var edited = await app.RawAsync(app.AdultA, HttpMethod.Put, arrangement, new
        {
            name = "Garde de Léa", carer = new { name = "Martine", pajemploi = "P123", approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4.30", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = "3.20",
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "18:00", lunch = true }).ToArray() } },
            },
            version,
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var days = (await GetAsync(app, url)).GetProperty("days");
        Assert.Equal("18:00", days[5].GetProperty("departure").GetString()); // Monday 6 October, untouched
        Assert.Equal("19:00", days[1].GetProperty("departure").GetString()); // Thursday 2 October, corrected
        Assert.False(days[5].GetProperty("changed").GetBoolean());
    }
}
