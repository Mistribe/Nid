using System.Net;
using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Childcare.IntegrationTests;

/// <summary>A full year (année complète) over the API. Today is 2025-10-15; the arrangement starts on 2025-09-01 (4,30 € net, Mon/Tue/Thu/Fri 08:00–17:30, 52 weeks).</summary>
public class ChildcareFullYearTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/childcare";

    private static object Body(NidApp app, int weeks = 52, object[]? gaps = null) => new
    {
        name = "Garde de Léa", childId = app.Dependent.Value,
        carer = new { name = "Martine", pajemploi = (string?)null, approval = (string?)null },
        terms = new
        {
            from = "2025-09-01", netHourlyRate = "4.30", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = "3.20", fullYear = true,
            weekTypes = new[] { new { name = "Semaine type", weeks, schedule = new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "17:30", lunch = true }).ToArray() } },
        },
        gaps = gaps ?? [], visibility = "shared",
        owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> GetAsync(NidApp app, string url)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>The month as read, the carer on leave on <paramref name="leave"/>, saved with a CMG of 0.</summary>
    private static async Task SaveWithLeaveAsync(NidApp app, string url, params string[] leave)
    {
        var month = await GetAsync(app, url);
        var days = month.GetProperty("days").EnumerateArray().Select(d =>
        {
            var off = leave.Contains(d.GetProperty("date").GetString());
            return new Dictionary<string, object?>
            {
                ["date"] = d.GetProperty("date").GetString(), ["present"] = !off && d.GetProperty("present").GetBoolean(),
                ["arrival"] = off ? null : d.GetProperty("arrival").GetString(), ["departure"] = off ? null : d.GetProperty("departure").GetString(),
                ["lunch"] = !off && d.GetProperty("lunch").GetBoolean(), ["absence"] = off ? "paid_leave" : d.GetProperty("absence").GetString(),
            };
        }).ToList();
        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { days, mileage = "0", cmg = "0", version = month.GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private static string Amount(JsonElement figures, string field) => figures.GetProperty(field).GetProperty("amount").GetString()!;

    [Fact]
    public async Task A_full_year_pays_52_weeks_and_deducts_leave_not_yet_accrued()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var arrangement = $"{Url}/{(await JsonAsync(created)).GetProperty("id").GetGuid()}";

        var detail = await GetAsync(app, arrangement);
        var terms = detail.GetProperty("terms")[0];
        Assert.True(terms.GetProperty("fullYear").GetBoolean());
        Assert.Equal("708.07", terms.GetProperty("monthlyBasePay").GetProperty("amount").GetString());
        Assert.Equal(0, detail.GetProperty("gapWeeksToPlace").GetInt32());

        // September: nothing accrued yet, four days of leave are taken without pay.
        var september = $"{arrangement}/statements/2025-09";
        await SaveWithLeaveAsync(app, september, "2025-09-08", "2025-09-09", "2025-09-11", "2025-09-12");
        var figures = (await GetAsync(app, september)).GetProperty("figures");
        Assert.Equal("708.07", Amount(figures, "basePay"));
        Assert.NotEqual("0.00", Amount(figures, "deduction"));
        Assert.Equal(JsonValueKind.Null, figures.GetProperty("declaration").GetProperty("paidLeaveDays").ValueKind);
        var validated = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{september}/validate", new { version = (await GetAsync(app, september)).GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);

        // October: September's 2,5 days, rounded up, cover a day of leave.
        var leave = (await GetAsync(app, arrangement)).GetProperty("leave");
        Assert.Equal((3, 0, 3), (leave.GetProperty("accrued").GetInt32(), leave.GetProperty("taken").GetInt32(), leave.GetProperty("left").GetInt32()));
        var october = $"{arrangement}/statements/2025-10";
        await SaveWithLeaveAsync(app, october, "2025-10-06");
        Assert.Equal("0.00", Amount((await GetAsync(app, october)).GetProperty("figures"), "deduction"));
    }

    [Fact]
    public async Task A_full_year_takes_52_weeks_and_no_week_without_care()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var short46 = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, weeks: 46));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, short46.StatusCode);
        Assert.Contains((await JsonAsync(short46)).GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "childcare.programmed_weeks_out_of_range");

        var gap = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, gaps: [new { monday = "2025-12-22", weeks = 1 }]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, gap.StatusCode);
        Assert.Contains((await JsonAsync(gap)).GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "childcare.gaps_too_many");
    }
}
