using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>
/// Spec 3E2 §2–§3: the real estate routine — what it proposes for a month, recording all or nothing, undoing. The host's today is
/// 2025-10-15: the default month is September 2025.
/// </summary>
public class PropertyRoutineTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

        // 9 days of 30 at 900, then 21 days of 30 at 600 and 50 of charges; the lease that ended on 1 September covers none of the month.
        Assert.Equal([("Ancien bail", "rent", "270.00", "2025-09-01"), ("Nouveau bail", "rent", "420.00", "2025-09-10"), ("Nouveau bail", "recoverable_charges", "35.00", "2025-09-10")],
            rents.Select(r => (S(r, "label"), S(r, "category"), S(r, "amount"), S(r, "occurredOn"))));
        Assert.All(rents, r => Assert.Equal(id, r.GetProperty("property").GetGuid()));
    }

    [Fact]
    public async Task A_tenancy_with_charges_gives_a_rent_line_and_a_charges_line()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "tenancies", new { label = "Avec", startsOn = "2021-01-01", rentMonthly = "800", chargesMonthly = "50" });
        var other = await NewPropertyAsync(app, Home(app, "Autre"));
        await PostAsync(app, other, "tenancies", new { label = "Sans", startsOn = "2021-01-01", rentMonthly = "500" });

        var rents = Lines(await ReadAsync(app), "rents");

        Assert.Equal([("Sans", "rent", "500.00"), ("Avec", "rent", "800.00"), ("Avec", "recoverable_charges", "50.00")],
            rents.Select(r => (S(r, "label"), S(r, "category"), S(r, "amount"))));
        Assert.All(rents, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("alreadyEntered").ValueKind));
    }

    [Fact]
    public async Task A_month_recorded_as_rent_with_charges_shows_both_lines_entered() // Review Focus #1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "tenancies", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "800", chargesMonthly = "50" });
        await PostAsync(app, id, "flows", new { occurredOn = "2025-09-05", kind = "income", category = "rent", amount = "850" });

        var rents = Lines(await ReadAsync(app), "rents");

        Assert.Equal([("rent", "850.00"), ("recoverable_charges", "50.00")], rents.Select(r => (S(r, "category"), S(r, "alreadyEntered"))));
    }

    [Fact]
    public async Task Rents_recorded_bare_leave_every_charges_line_of_a_property_open() // Review Focus #1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app, "Locatif"));
        await PostAsync(app, id, "tenancies", new { label = "Ancien bail", startsOn = "2021-01-01", endsOn = "2025-09-10", rentMonthly = "900", chargesMonthly = "50" });
        await PostAsync(app, id, "tenancies", new { label = "Nouveau bail", startsOn = "2025-09-10", rentMonthly = "600", chargesMonthly = "50" });
        await PostAsync(app, id, "flows", new { occurredOn = "2025-09-01", kind = "income", category = "rent", amount = "270" });
        await PostAsync(app, id, "flows", new { occurredOn = "2025-09-10", kind = "income", category = "rent", amount = "420" });

        var rents = Lines(await ReadAsync(app), "rents");

        Assert.Equal([("Ancien bail", "rent", "690.00"), ("Ancien bail", "recoverable_charges", null), ("Nouveau bail", "rent", "690.00"), ("Nouveau bail", "recoverable_charges", null)], // rent rows sum the month, as before
            rents.Select(r => (S(r, "label"), S(r, "category"), S(r, "alreadyEntered"))));
    }

    [Fact]
    public async Task A_recoverable_charges_flow_marks_the_charges_line_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "tenancies", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "800", chargesMonthly = "50" });
        await PostAsync(app, id, "flows", new { occurredOn = "2025-09-05", kind = "income", category = "rent", amount = "800" });
        await PostAsync(app, id, "flows", new { occurredOn = "2025-09-05", kind = "income", category = "recoverable_charges", amount = "45" });

        var rents = Lines(await ReadAsync(app), "rents");

        Assert.Equal([("rent", "800.00"), ("recoverable_charges", "45.00")], rents.Select(r => (S(r, "category"), S(r, "alreadyEntered"))));
    }

    [Fact]
    public async Task The_routine_records_tenant_charges()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var estimate = Estimate(id, "300000");

        var response = await RecordAsync(app, new { on = "2025-09-30", estimates = new[] { estimate }, flows = new[] { Flow(id, "income", "recoverable_charges", "2025-09-01", "50") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("recoverable_charges", S(Assert.Single(Lines(Assert.Single(Lines(await JsonAsync(response), "lines")), "flows")), "category"));
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

    private static object Estimate(Guid property, string amount) => new { property, amount };

    private static object Flow(Guid property, string kind, string category, string occurredOn, string amount)
        => new { property, kind, category, occurredOn, amount };

    private static async Task<HttpResponseMessage> RecordAsync(NidApp app, object body) => await app.RawAsync(app.AdultA, HttpMethod.Post, Url, body);

    [Fact]
    public async Task Recording_writes_the_estimates_and_the_ticked_lines_and_answers_each_facts_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app, "Maison"));
        var rental = await NewPropertyAsync(app, Home(app, "Locatif"));

        var response = await RecordAsync(app, new
        {
            on = "2025-09-30",
            estimates = new[] { Estimate(home, "300000") },
            flows = new[] { Flow(home, "expense", "property_tax", "2025-09-20", "1200"), Flow(rental, "income", "rent", "2025-09-01", "950.5") },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recap = await JsonAsync(response);
        Assert.Equal("2025-09-30", S(recap, "on"));
        var lines = Lines(recap, "lines");
        Assert.Equal(["Locatif", "Maison"], lines.Select(l => S(l, "name")));
        Assert.Equal((JsonValueKind.Null, (string?)null), (lines[0].GetProperty("valuationId").ValueKind, S(lines[0], "estimate")));
        var rent = Assert.Single(Lines(lines[0], "flows"));
        Assert.Equal(("income", "rent", "2025-09-01", "950.50"), (S(rent, "kind"), S(rent, "category"), S(rent, "occurredOn"), S(rent, "amount")));
        Assert.Equal(("300000.00", "EUR"), (S(lines[1], "estimate"), S(lines[1], "currency")));

        var detail = await PropertyAsync(app, home);
        var valuation = Only(detail, "valuations");
        Assert.Equal((lines[1].GetProperty("valuationId").GetGuid(), "2025-09-30", "300000.00"),
            (valuation.GetProperty("id").GetGuid(), S(valuation, "asOf"), S(valuation, "amount")));
        var tax = Only(detail, "flows");
        Assert.Equal((Lines(lines[1], "flows")[0].GetProperty("flowId").GetGuid(), "expense", "property_tax", "1200.00"),
            (tax.GetProperty("id").GetGuid(), S(tax, "kind"), S(tax, "category"), S(tax, "amount")));
        Assert.Equal(("950.50", "2025-09-01"), (S(Only(await PropertyAsync(app, rental), "flows"), "amount"), S(Only(await PropertyAsync(app, rental), "flows"), "occurredOn")));

        // Read again, the month says what was entered and the estimate is no longer due.
        var view = await ReadAsync(app);
        Assert.All(Lines(view, "estimates"), e => Assert.Equal(S(e, "name") == "Locatif", e.GetProperty("due").GetBoolean()));
    }

    [Theory]
    [InlineData("nothing", "estimates", "realestate.routine_empty")]
    [InlineData("future routine", "on", "core.date_in_future")]
    [InlineData("zero estimate", "estimates[1].amount", "realestate.invalid_property_field")]
    [InlineData("three decimals", "estimates[1].amount", "realestate.invalid_property_field")]
    [InlineData("same property twice", "estimates[1]", "realestate.routine_duplicate")]
    [InlineData("unknown kind", "flows[0].kind", "realestate.invalid_category")]
    [InlineData("works", "flows[0].category", "realestate.invalid_category")]
    [InlineData("other income", "flows[0].category", "realestate.invalid_category")]
    [InlineData("outside the month", "flows[0].occurredOn", "realestate.invalid_property_field")]
    [InlineData("after today", "flows[0].occurredOn", "core.date_in_future")] // Review Focus #3
    [InlineData("negative flow", "flows[0].amount", "realestate.invalid_property_field")]
    [InlineData("no amount", "estimates[1].amount", "request.required")]
    public async Task A_refusal_names_its_field_and_nothing_is_written(string why, string path, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await NewPropertyAsync(app, Home(app, "Premier"));
        var second = await NewPropertyAsync(app, Home(app, "Second"));
        var good = Estimate(first, "300000");
        object body = why switch
        {
            "nothing" => new { on = "2025-09-30", estimates = Array.Empty<object>(), flows = Array.Empty<object>() },
            "future routine" => new { on = "2025-10-16", estimates = new[] { good } },
            "zero estimate" => new { on = "2025-09-30", estimates = new[] { good, Estimate(second, "0") } },
            "three decimals" => new { on = "2025-09-30", estimates = new[] { good, Estimate(second, "1.005") } },
            "same property twice" => new { on = "2025-09-30", estimates = new[] { good, Estimate(first, "1") } },
            "unknown kind" => new { on = "2025-09-30", estimates = new[] { good }, flows = new[] { Flow(first, "contribution", "property_tax", "2025-09-20", "1") } },
            "works" => new { on = "2025-09-30", estimates = new[] { good }, flows = new[] { Flow(first, "expense", "works", "2025-09-20", "1") } },
            "other income" => new { on = "2025-09-30", estimates = new[] { good }, flows = new[] { Flow(first, "income", "other_income", "2025-09-20", "1") } },
            "outside the month" => new { on = "2025-09-30", estimates = new[] { good }, flows = new[] { Flow(first, "expense", "property_tax", "2025-08-31", "1") } },
            // The current month: a cost due on the 20th, later than today, recorded as listed.
            "after today" => new { on = "2025-10-15", estimates = new[] { good }, flows = new[] { Flow(first, "expense", "property_tax", "2025-10-20", "1200") } },
            "negative flow" => new { on = "2025-09-30", estimates = new[] { good }, flows = new[] { Flow(first, "expense", "property_tax", "2025-09-20", "-1") } },
            _ => new { on = "2025-09-30", estimates = new[] { good, new { property = second } } },
        };

        Assert.Equal((HttpStatusCode.UnprocessableEntity, code, path), await RefusalAsync(await RecordAsync(app, body)));
        var detail = await PropertyAsync(app, first);
        Assert.Empty(detail.GetProperty("valuations").EnumerateArray());
        Assert.Empty(detail.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task Two_flows_of_one_category_on_one_date_are_both_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app, "Maison"));

        var response = await RecordAsync(app, new
        {
            on = "2025-09-30",
            flows = new[] { Flow(home, "expense", "energy", "2025-09-20", "80"), Flow(home, "expense", "energy", "2025-09-20", "60") },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(Lines(await JsonAsync(response), "lines"));
        Assert.Equal(["60.00", "80.00"], Lines(line, "flows").Select(f => S(f, "amount")).Order());
    }

    [Fact]
    public async Task An_estimate_already_at_on_is_refused_and_nothing_else_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "valuations", new { asOf = "2025-09-30", amount = "290000" });

        var refused = await RecordAsync(app, new
        {
            on = "2025-09-30", estimates = new[] { Estimate(id, "300000") }, flows = new[] { Flow(id, "expense", "property_tax", "2025-09-20", "1200") },
        });

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.routine_value_exists", "estimates[0].amount"), await RefusalAsync(refused));
        var detail = await PropertyAsync(app, id);
        Assert.Equal("290000.00", S(Only(detail, "valuations"), "amount"));
        Assert.Empty(detail.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task A_property_the_reader_does_not_write_is_not_found_and_nothing_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await NewPropertyAsync(app, Home(app, "Mien"));
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        var bobs = await NewPropertyAsync(app, Home(app, "À Bob", "shared", app.AdultB), app.AdultB);
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        foreach (var other in new[] { secret, bobs, envelope.Value, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await RecordAsync(app, new
            {
                on = "2025-09-30", estimates = new[] { Estimate(mine, "300000") }, flows = new[] { Flow(other, "expense", "property_tax", "2025-09-20", "1200") },
            })).StatusCode);

        Assert.Empty((await PropertyAsync(app, mine)).GetProperty("valuations").EnumerateArray());
        Assert.Empty((await PropertyAsync(app, bobs)).GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task A_property_in_another_currency_records_in_its_own()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app, "Miami");
        body["currency"] = "USD";
        var id = await NewPropertyAsync(app, body);

        var response = await RecordAsync(app, new { on = "2025-09-30", estimates = new[] { Estimate(id, "450000") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the Core refuses an amount in another currency than the item's
        var line = Lines(await JsonAsync(response), "lines")[0];
        Assert.Equal(("USD", "450000.00"), (S(line, "currency"), S(line, "estimate")));
        Assert.Equal("450000.00", S(Only(await PropertyAsync(app, id), "valuations"), "amount"));
    }

    [Fact]
    public async Task Undo_voids_exactly_what_the_routine_recorded_and_refuses_twice()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "valuations", new { asOf = "2025-06-01", amount = "290000" });
        var line = Lines(await JsonAsync(await RecordAsync(app, new
        {
            on = "2025-09-30", estimates = new[] { Estimate(id, "300000") }, flows = new[] { Flow(id, "expense", "property_tax", "2025-09-20", "1200") },
        })), "lines")[0];
        var facts = new object[]
        {
            new { property = id, valuationId = line.GetProperty("valuationId").GetGuid() },
            new { property = id, flowId = Lines(line, "flows")[0].GetProperty("flowId").GetGuid() },
        };

        var undo = await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new { facts });

        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(2, (await JsonAsync(undo)).GetProperty("voided").GetInt32());
        var detail = await PropertyAsync(app, id);
        Assert.Equal(["2025-06-01"], detail.GetProperty("valuations").EnumerateArray().Select(v => S(v, "asOf")));
        Assert.Empty(detail.GetProperty("flows").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new { facts })).StatusCode); // already voided
    }

    [Fact]
    public async Task Undo_refuses_a_property_the_reader_does_not_write_and_another_items_facts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private"));
        var line = Lines(await JsonAsync(await RecordAsync(app, new { on = "2025-09-30", estimates = new[] { Estimate(secret, "300000") } })), "lines")[0];
        var valuation = line.GetProperty("valuationId").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Post, Url + "/undo",
            new { facts = new[] { new { property = secret, valuationId = valuation } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo",
            new { facts = new[] { new { property = envelope.Value, valuationId = valuation } } })).StatusCode);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.routine_empty", "facts"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new { facts = Array.Empty<object>() })));
        Assert.Equal("300000.00", S(Only(await PropertyAsync(app, secret), "valuations"), "amount"));
    }
}
