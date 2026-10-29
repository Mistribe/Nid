using System.Net;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Spec 3C1 §2 and §4: a property's estimates and flows, the category convention, and the figures they shape.</summary>
public class PropertyFactTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> PostAsync(NidApp app, Guid property, string route, object body)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/{route}", body));

    [Fact]
    public async Task Estimates_and_flows_read_back_by_date_and_shape_the_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "valuations", new { asOf = "2025-06-01", amount = "290000", note = "estimation en ligne" });
        await PostAsync(app, id, "valuations", new { asOf = "2024-06-01", amount = "280000" });
        foreach (var (occurredOn, kind, amount, category) in new[]
                 {
                     ("2021-03-01", "expense", "12000", "works"), ("2024-10-15", "expense", "1500", "property_tax"),
                     ("2025-09-05", "income", "900", "rent"), ("2025-10-05", "income", "900", "rent"), ("2025-10-06", "income", "50", "other_income"),
                 })
            await PostAsync(app, id, "flows", new { occurredOn, kind, amount, category });

        var detail = await PropertyAsync(app, id);
        var valuations = detail.GetProperty("valuations");
        Assert.Equal(["2024-06-01", "2025-06-01"], valuations.EnumerateArray().Select(v => S(v, "asOf")));
        Assert.Equal(("290000.00", "estimation en ligne"), (S(valuations[1], "amount"), S(valuations[1], "note")));
        var flows = detail.GetProperty("flows").EnumerateArray().ToList();
        Assert.Equal(["works", "property_tax", "rent", "rent", "other_income"], flows.Select(f => S(f, "category")));
        Assert.Equal(("expense", "12000.00"), (S(flows[0], "kind"), S(flows[0], "amount")));

        var figures = detail.GetProperty("figures");
        Assert.Equal(("290000.00", "2025-06-01", false), (S(figures, "value"), S(figures, "valueOn"), figures.GetProperty("stale").GetBoolean()));
        Assert.Equal(("281000.00", "9000.00", "290000.00"), (S(figures, "costPrice"), S(figures, "latentGain"), S(figures, "netValue")));
        Assert.Equal(("282500.00", "280700.00"), (S(figures, "realCost"), S(figures, "netCostOfRents")));
        Assert.Equal(["acquisition", "loan_interest", "loan_insurance", "loan_fees", "works", "property_tax"],
            figures.GetProperty("realCostLines").EnumerateArray().Select(l => S(l, "code")));
        Assert.Empty(detail.GetProperty("warnings").EnumerateArray());

        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.Equal(("290000.00", "2025-06-01", "290000.00"), (S(row, "value"), S(row, "valueOn"), S(row, "netValue")));
    }

    [Fact]
    public async Task A_flows_kind_and_category_follow_the_convention_and_each_refusal_names_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        const string Category = "realestate.invalid_category";
        const string Field = "realestate.invalid_property_field";
        var cases = new (string Why, string Route, object Body, string Code, string Path)[]
        {
            ("a contribution", "flows", new { occurredOn = "2025-06-01", kind = "contribution", amount = "100", category = "works" }, Category, "kind"),
            ("no kind", "flows", new { occurredOn = "2025-06-01", amount = "100", category = "works" }, Category, "kind"),
            ("rent spent", "flows", new { occurredOn = "2025-06-01", kind = "expense", amount = "100", category = "rent" }, Category, "category"),
            ("works earned", "flows", new { occurredOn = "2025-06-01", kind = "income", amount = "100", category = "works" }, Category, "category"),
            ("no category", "flows", new { occurredOn = "2025-06-01", kind = "expense", amount = "100" }, Category, "category"),
            ("a free flow", "flows", new { occurredOn = "2025-06-01", kind = "expense", amount = "0", category = "works" }, Field, "amount"),
            ("a third decimal", "flows", new { occurredOn = "2025-06-01", kind = "expense", amount = "100.005", category = "works" }, Field, "amount"),
            ("a French decimal comma", "flows", new { occurredOn = "2025-06-01", kind = "expense", amount = "100,5", category = "works" }, "request.amount", "amount"),
            ("a flow tomorrow", "flows", new { occurredOn = "2025-10-16", kind = "expense", amount = "100", category = "works" }, "core.date_in_future", "occurredOn"),
            ("an estimate tomorrow", "valuations", new { asOf = "2025-10-16", amount = "290000" }, "core.date_in_future", "asOf"),
            ("an estimate of nothing", "valuations", new { asOf = "2025-06-01", amount = "0" }, Field, "amount"),
            ("an estimate without a date", "valuations", new { amount = "290000" }, "request.required", "asOf"),
        };

        foreach (var (why, route, body, code, path) in cases)
        {
            var (status, gotCode, gotPath) = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/{route}", body));
            Assert.True((status, gotCode, gotPath) == (HttpStatusCode.UnprocessableEntity, code, path), $"{why}: got {status} {gotCode} at {gotPath}");
        }

        var detail = await PropertyAsync(app, id);
        Assert.Empty(detail.GetProperty("valuations").EnumerateArray());
        Assert.Empty(detail.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task Facts_are_corrected_and_voided_and_an_estimate_on_a_date_already_estimated_replaces_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "valuations", new { asOf = "2025-06-01", amount = "290000" });
        await PostAsync(app, id, "valuations", new { asOf = "2025-06-01", amount = "295000" });
        var flow = await PostAsync(app, id, "flows", new { occurredOn = "2021-03-01", kind = "expense", amount = "12000", category = "works" });

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{id}/flows/{flow}",
            new { occurredOn = "2021-03-01", kind = "expense", amount = "12500", category = "works", note = "facture finale" });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        var detail = await PropertyAsync(app, id);
        Assert.Equal("295000.00", S(Only(detail, "valuations"), "amount"));
        var current = Only(detail, "flows");
        Assert.Equal(("12500.00", "facture finale"), (S(current, "amount"), S(current, "note")));
        Assert.Equal("281500.00", S(detail.GetProperty("figures"), "costPrice"));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Properties}/{id}/flows/{current.GetProperty("id").GetGuid()}")).StatusCode);
        var valuation = Only(detail, "valuations").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Properties}/{id}/valuations/{valuation}")).StatusCode);

        detail = await PropertyAsync(app, id);
        Assert.Empty(detail.GetProperty("valuations").EnumerateArray());
        Assert.Empty(detail.GetProperty("flows").EnumerateArray());
        Assert.Equal("no_valuation", S(Only(detail, "warnings"), "code"));
    }

    [Fact]
    public async Task An_estimate_turns_stale_past_its_threshold()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, id, "valuations", new { asOf = "2024-09-09", amount = "280000" }); // 401 days before 2025-10-15

        var detail = await PropertyAsync(app, id);
        Assert.True(detail.GetProperty("figures").GetProperty("stale").GetBoolean());
        Assert.Equal("valuation_stale", S(Only(detail, "warnings"), "code"));
        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.True(row.GetProperty("stale").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}",
            new { version = await VersionAsync(app, id), staleAfterDays = 500 })).StatusCode);
        detail = await PropertyAsync(app, id);
        Assert.False(detail.GetProperty("figures").GetProperty("stale").GetBoolean());
        Assert.Empty(detail.GetProperty("warnings").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}",
            new { version = await VersionAsync(app, id), clearStaleAfterDays = true, valuationCadence = "manual" })).StatusCode);
        Assert.False((await PropertyAsync(app, id)).GetProperty("figures").GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task A_fact_of_another_property_is_not_found_through_this_one_and_stays() // Review Focus #3
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        var flat = await NewPropertyAsync(app, Home(app, name: "Appartement"));
        var valuation = await PostAsync(app, flat, "valuations", new { asOf = "2025-06-01", amount = "150000" });
        var flow = await PostAsync(app, flat, "flows", new { occurredOn = "2025-06-01", kind = "expense", amount = "800", category = "property_tax" });

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Delete, $"{Properties}/{home}/valuations/{valuation}", null),
                     (HttpMethod.Put, $"{Properties}/{home}/flows/{flow}", new { occurredOn = "2025-06-01", kind = "expense", amount = "1", category = "property_tax" }),
                     (HttpMethod.Delete, $"{Properties}/{home}/flows/{flow}", null),
                 })
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, method, url, body)).StatusCode);

        var detail = await PropertyAsync(app, flat);
        Assert.Equal(valuation, Only(detail, "valuations").GetProperty("id").GetGuid());
        Assert.Equal((flow, "800.00"), (Only(detail, "flows").GetProperty("id").GetGuid(), S(Only(detail, "flows"), "amount")));
    }

    [Fact]
    public async Task A_flow_corrected_to_the_other_kind_keeps_no_category_of_its_old_kind() // Review Focus #4
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var flow = await PostAsync(app, id, "flows", new { occurredOn = "2021-03-01", kind = "expense", amount = "12000", category = "works" });

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_category", "category"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{id}/flows/{flow}",
                new { occurredOn = "2021-03-01", kind = "income", amount = "12000", category = "works" })));

        var stored = Only(await PropertyAsync(app, id), "flows");
        Assert.Equal((flow, "expense", "works"), (stored.GetProperty("id").GetGuid(), S(stored, "kind"), S(stored, "category")));
    }
}
