using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Income;

/// <summary>Income dashboard spec §4: a source's page, its figures, months per component and changes. Today is 2025-10-15.</summary>
[Collection("postgres")]
public class SourceOverviewEndpointTests(PostgresFixture fixture)
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label, string startsOn, string amount, string unit = "month")
        => new { label, startsOn, netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit } };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Guid> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> NewSourceAsync(NidApp app, params object[] phases)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = phases,
        }));

    private static async Task<JsonElement> OverviewAsync(NidApp app, Guid id, string query = "")
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/overview{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string? Amount(JsonElement view) => view.ValueKind == JsonValueKind.Null ? null : view.GetProperty("amount").GetString();

    private static decimal Number(JsonElement view) => decimal.Parse(Amount(view)!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Source_overview_stacks_each_label_per_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2024-01-28", "2000"), Phase("Prime", "2024-12-15", "1800", unit: "year"));

        var overview = await OverviewAsync(app, id);

        Assert.Equal(("2024-11-01", "2026-01-31", "EUR"),
            (overview.GetProperty("from").GetString(), overview.GetProperty("to").GetString(), overview.GetProperty("currency").GetString()));
        var months = overview.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(15, months.Count);
        var december = months[1];
        Assert.Equal((2024, 12), (december.GetProperty("year").GetInt32(), december.GetProperty("month").GetInt32()));
        Assert.Equal([("Prime", 1800m), ("Salaire", 2000m)],
            december.GetProperty("parts").EnumerateArray().Select(p => (p.GetProperty("label").GetString(), Number(p.GetProperty("amount")))));
        Assert.Equal(3800m, Number(december.GetProperty("total"))); // an IncomeMoneyView keeps the stored scale ("3800.0000"), as /expected's total
        Assert.False(december.GetProperty("upcoming").GetBoolean());
        Assert.True(months[13].GetProperty("upcoming").GetBoolean());
        Assert.Equal(3800m, Number(months[13].GetProperty("total")));
        var figures = overview.GetProperty("figures");
        Assert.Equal("2150.00", Amount(figures.GetProperty("monthlyNow")));
        Assert.Equal([("Prime", "150.00"), ("Salaire", "2000.00")],
            figures.GetProperty("monthlyParts").EnumerateArray().Select(p => (p.GetProperty("name").GetString(), Amount(p.GetProperty("amount")))));

        Assert.Equal("2024-01-01", (await OverviewAsync(app, id, "?period=all")).GetProperty("from").GetString());
        Assert.Equal(39, (await OverviewAsync(app, id, "?period=3y")).GetProperty("months").GetArrayLength());
        var invalid = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{id}/overview?period=year");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("income.invalid_period", (await JsonAsync(invalid)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Source_overview_gives_next_payment_last_payments_and_last_one_offs()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2025-01-28", "2000"));
        for (var month = 1; month <= 6; month++)
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/one-offs",
                new { occurredOn = $"2025-0{month}-10", amount = new { amount = $"{month}00", currency = "EUR" }, note = "Prime" }));

        var overview = await OverviewAsync(app, id);

        var figures = overview.GetProperty("figures");
        Assert.Equal(("2025-10-28", 2000m, "Salaire"), (figures.GetProperty("nextOn").GetString(), Number(figures.GetProperty("nextAmount")),
            figures.GetProperty("nextLabel").GetString()));
        Assert.Equal((20_100m, 2_100m), (Number(figures.GetProperty("receivedThisYear")), Number(figures.GetProperty("oneOffsThisYear"))));
        Assert.Equal(["2025-09-28", "2025-08-28", "2025-07-28", "2025-06-28", "2025-05-28"],
            overview.GetProperty("lastPayments").EnumerateArray().Select(p => p.GetProperty("on").GetString()));
        Assert.Equal(["2025-06-10", "2025-05-10", "2025-04-10", "2025-03-10", "2025-02-10"],
            overview.GetProperty("lastOneOffs").EnumerateArray().Select(p => p.GetProperty("occurredOn").GetString()));
        Assert.Equal(300m, Number(overview.GetProperty("months").EnumerateArray()
            .Single(m => m.GetProperty("month").GetInt32() == 3 && m.GetProperty("year").GetInt32() == 2025).GetProperty("oneOff")));
    }

    [Fact]
    public async Task A_source_started_this_year_has_no_change_over_one_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2025-01-01", "2000"));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Sources}/{id}/phase-changes",
            new { label = "Salaire", effectiveOn = "2025-06-01", netAmount = new { amount = "2100", currency = "EUR" } }));

        var overview = await OverviewAsync(app, id);

        var figures = overview.GetProperty("figures");
        Assert.Equal(JsonValueKind.Null, figures.GetProperty("change1y").ValueKind);
        Assert.Equal(5.0m, figures.GetProperty("changeSinceStart").GetDecimal());
        var changes = overview.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal([("change", "2025-06-01"), ("start", "2025-01-01")],
            changes.Select(c => (c.GetProperty("kind").GetString(), c.GetProperty("on").GetString())));
        Assert.Equal((2000m, 2100m, 5.0m), (Number(changes[0].GetProperty("before")), Number(changes[0].GetProperty("after")),
            changes[0].GetProperty("percent").GetDecimal()));
    }

    [Fact]
    public async Task The_other_adults_private_source_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewIncomeSourceAsync("Secret de B", Visibility.Private, (app.AdultB, 10_000));

        var answer = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{hidden.Value}/overview");
        var unused = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Sources}/{Guid.NewGuid()}/overview");

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        // Exactly like a source nobody uses: the answer gives nothing away.
        Assert.Equal((unused.StatusCode, (await JsonAsync(unused)).GetProperty("code").GetString()),
            (answer.StatusCode, (await JsonAsync(answer)).GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Labels_paid_on_the_same_next_date_are_named_together()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewSourceAsync(app, Phase("Salaire", "2025-01-28", "2000"), Phase("Prime", "2024-10-28", "1800", unit: "year"));

        var figures = (await OverviewAsync(app, id)).GetProperty("figures");

        Assert.Equal(("2025-10-28", 3800m, "Prime + Salaire"), (figures.GetProperty("nextOn").GetString(),
            Number(figures.GetProperty("nextAmount")), figures.GetProperty("nextLabel").GetString()));
    }
}
