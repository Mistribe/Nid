using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>
/// Spec 3E2 §2–§3: the real estate routine — what it proposes for a month, recording all or nothing, undoing. The host's today is
/// 2025-10-15: the default month is September 2025.
/// </summary>
[Collection("postgres")]
public class PropertyRoutineTests(PostgresFixture fixture)
{
    private const string Url = "/api/v1/realestate/routine";

    private static async Task PostAsync(NidApp app, Guid property, string route, object body)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/{route}", body)).StatusCode);

    private static async Task<JsonElement> ReadAsync(NidApp app, string query = "", PersonId? reader = null)
    {
        var response = await app.RawAsync(reader ?? app.AdultA, HttpMethod.Get, Url + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static List<JsonElement> Lines(JsonElement view, string list) => view.GetProperty(list).EnumerateArray().ToList();

    [Fact]
    public async Task Estimates_default_to_the_end_of_last_month_due_ones_first_with_their_latest()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var stale = await NewPropertyAsync(app, Home(app, "Zèbre"));
        await PostAsync(app, stale, "valuations", new { asOf = "2024-06-01", amount = "280000" }); // 486 days before 2025-09-30: over 400
        var recent = await NewPropertyAsync(app, Home(app, "À jour"));
        await PostAsync(app, recent, "valuations", new { asOf = "2025-06-01", amount = "290000" });
        await PostAsync(app, recent, "valuations", new { asOf = "2025-10-01", amount = "295000" }); // after `on`: not its latest at `on`
        await NewPropertyAsync(app, Home(app, "Neuf"));

        var view = await ReadAsync(app);

        Assert.Equal("2025-09-30", S(view, "on"));
        var estimates = Lines(view, "estimates");
        Assert.Equal(["Neuf", "Zèbre", "À jour"], estimates.Select(e => S(e, "name")));
        Assert.Equal([true, true, false], estimates.Select(e => e.GetProperty("due").GetBoolean()));
        Assert.Equal(((string?)null, (string?)null), (S(estimates[0], "lastValue"), S(estimates[0], "lastAsOf")));
        Assert.Equal(("280000.00", "2024-06-01", "EUR"), (S(estimates[1], "lastValue"), S(estimates[1], "lastAsOf"), S(estimates[1], "currency")));
        Assert.Equal(("290000.00", "2025-06-01"), (S(estimates[2], "lastValue"), S(estimates[2], "lastAsOf")));
        Assert.Equal(recent, estimates[2].GetProperty("property").GetGuid());
    }

    [Fact]
    public async Task A_cost_is_listed_in_the_months_it_falls_in_only() // Review Focus #2 and #3
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        foreach (var (category, amount, count, unit, startsOn) in new[]
                 {
                     ("condo_fees", "300", 3, "month", "2025-03-10"),   // 03-10, 06-10, 09-10, 12-10
                     ("insurance", "150", 3, "month", "2025-01-10"),    // 01-10, 04-10, 07-10, 10-10
                     ("energy", "80", 1, "month", "2025-01-20"),        // every 20th
                     ("property_tax", "1200", 1, "year", "2020-09-25"), // every 25 September
                 })
            await PostAsync(app, id, "recurring-costs", new { category, amount, every = new { count, unit }, startsOn });

        var september = Lines(await ReadAsync(app), "costs");
        Assert.Equal([("condo_fees", "300.00", "2025-09-10"), ("energy", "80.00", "2025-09-20"), ("property_tax", "1200.00", "2025-09-25")],
            september.Select(c => (S(c, "category"), S(c, "amount"), S(c, "dueOn"))));
        Assert.All(september, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("alreadyEntered").ValueKind));

        // The current month: the quarter's insurance on the 10th, and the energy on the 20th, after today, listed with its own date.
        var october = Lines(await ReadAsync(app, "?on=2025-10-15"), "costs");
        Assert.Equal([("insurance", "2025-10-10"), ("energy", "2025-10-20")], october.Select(c => (S(c, "category"), S(c, "dueOn"))));
    }

    [Fact]
    public async Task A_rent_covering_part_of_the_month_is_prorated_and_dated_on_its_first_day() // Review Focus #1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app, "Locatif"));
        await PostAsync(app, id, "tenancies", new { label = "Ancien bail", startsOn = "2021-01-01", endsOn = "2025-09-10", rentMonthly = "900" });
        await PostAsync(app, id, "tenancies", new { label = "Nouveau bail", startsOn = "2025-09-10", rentMonthly = "600", chargesMonthly = "50" });
        var sold = await NewPropertyAsync(app, Home(app, "Vendu"));
        await PostAsync(app, sold, "tenancies", new { label = "Fini", startsOn = "2021-01-01", endsOn = "2025-09-01", rentMonthly = "700" });

        var rents = Lines(await ReadAsync(app), "rents");

        // 9 days of 30 at 900, then 21 days of 30 at 650; the lease that ended on 1 September covers none of the month.
        Assert.Equal([("Ancien bail", "270.00", "2025-09-01"), ("Nouveau bail", "455.00", "2025-09-10")],
            rents.Select(r => (S(r, "label"), S(r, "amount"), S(r, "occurredOn"))));
        Assert.All(rents, r => Assert.Equal(id, r.GetProperty("property").GetGuid()));
    }

    [Fact]
    public async Task A_line_says_what_was_already_entered_in_the_month_for_its_kind_and_category()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "recurring-costs", new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2020-09-20" });
        await PostAsync(app, id, "recurring-costs", new { category = "insurance", amount = "40", every = new { count = 1, unit = "month" }, startsOn = "2021-01-05" });
        await PostAsync(app, id, "tenancies", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900" });
        foreach (var (occurredOn, kind, category, amount) in new[]
                 {
                     ("2025-09-05", "income", "rent", "900"), ("2025-09-25", "income", "rent", "100"), ("2025-08-05", "income", "rent", "900"),
                     ("2025-09-20", "expense", "property_tax", "1150"), ("2025-09-06", "expense", "other", "40"),
                 })
            await PostAsync(app, id, "flows", new { occurredOn, kind, category, amount });

        var view = await ReadAsync(app);

        Assert.Equal("1000.00", S(Assert.Single(Lines(view, "rents")), "alreadyEntered")); // September's rents only, summed
        Assert.Equal([("insurance", (string?)null), ("property_tax", "1150.00")],
            Lines(view, "costs").Select(c => (S(c, "category"), S(c, "alreadyEntered"))));
    }

    [Fact]
    public async Task Only_the_properties_the_reader_writes_are_listed_and_a_future_date_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewPropertyAsync(app, Home(app, "Mien"));
        await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        await NewPropertyAsync(app, Home(app, "À Bob", "shared", app.AdultB), app.AdultB); // A reads it, only B writes it

        Assert.Equal(["Mien"], Lines(await ReadAsync(app), "estimates").Select(e => S(e, "name")));
        Assert.Equal(["À Bob", "Secret"], Lines(await ReadAsync(app, reader: app.AdultB), "estimates").Select(e => S(e, "name")));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "core.date_in_future", "on"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url + "?on=2025-10-16")));
    }

    [Fact]
    public async Task A_property_in_another_currency_proposes_in_its_own()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app, "Miami");
        body["currency"] = "USD";
        var id = await NewPropertyAsync(app, body);
        await PostAsync(app, id, "tenancies", new { label = "Condo", startsOn = "2021-01-01", rentMonthly = "2000" });

        var view = await ReadAsync(app);

        Assert.Equal("USD", S(Assert.Single(Lines(view, "estimates")), "currency"));
        var rent = Assert.Single(Lines(view, "rents"));
        Assert.Equal(("USD", "2000.00"), (S(rent, "currency"), S(rent, "amount")));
    }
}
