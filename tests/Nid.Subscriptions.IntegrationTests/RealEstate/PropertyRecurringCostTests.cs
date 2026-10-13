using System.Net;
using System.Text.Json;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3C2 §2–§3: a property's planned costs, their monthly equivalent and next due date, versions and refusals.</summary>
[Collection("postgres")]
public class PropertyRecurringCostTests(PostgresFixture fixture)
{
    private static object Tax(uint? version = null) => new
    {
        category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2021-10-15", note = "taxe foncière", version,
    };

    [Fact]
    public async Task A_recurring_cost_is_added_corrected_and_deleted_against_its_version()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var costs = $"{Properties}/{id}/recurring-costs";

        var cost = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, costs, Tax()));
        var view = Only(await PropertyAsync(app, id), "recurringCosts");
        Assert.Equal((cost, "property_tax", "1200.00", "2021-10-15", (string?)null, "taxe foncière"),
            (view.GetProperty("id").GetGuid(), S(view, "category"), S(view, "amount"), S(view, "startsOn"), S(view, "endsOn"), S(view, "note")));
        Assert.Equal((1, "year"), (view.GetProperty("every").GetProperty("count").GetInt32(), S(view.GetProperty("every"), "unit")));
        Assert.Equal(("100.00", "2025-10-15"), (S(view, "monthlyEquivalent"), S(view, "nextDueOn"))); // due on the host's today
        var version = view.GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{costs}/{cost}", new
        {
            category = "condo_fees", amount = "250", every = new { count = 3, unit = "month" }, startsOn = "2025-01-01", version,
        })).StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "realestate.recurring_cost_conflict", (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{costs}/{cost}", Tax(version))));
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{costs}/{cost}?version={version}")).StatusCode);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{costs}/{cost}", Tax())));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{costs}/{cost}")));
        // Shared, A's alone: B reads it and cannot change it.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, costs, Tax())).StatusCode);

        // A second cost, ended: listed first (by start date), with no next due date.
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, costs, new
        {
            category = "insurance", amount = "300", every = new { count = 1, unit = "year" }, startsOn = "2020-07-01", endsOn = "2024-07-01",
        }));
        var listed = (await PropertyAsync(app, id)).GetProperty("recurringCosts").EnumerateArray().ToList();
        Assert.Equal(["insurance", "condo_fees"], listed.Select(c => S(c, "category")));
        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("nextDueOn").ValueKind);
        Assert.Equal(("250.00", "83.33", "2026-01-01"), (S(listed[1], "amount"), S(listed[1], "monthlyEquivalent"), S(listed[1], "nextDueOn")));

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete,
            $"{costs}/{cost}?version={listed[1].GetProperty("version").GetUInt32()}")).StatusCode);
        Assert.Equal(["insurance"], (await PropertyAsync(app, id)).GetProperty("recurringCosts").EnumerateArray().Select(c => S(c, "category")));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{costs}/{cost}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Each_recurring_cost_refusal_answers_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        const string Field = "realestate.invalid_property_field";
        const string Category = "realestate.invalid_category";
        var year = new { count = 1, unit = "year" };
        var cases = new (string Why, object Body, string Code, string Path)[]
        {
            ("works", new { category = "works", amount = "1200", every = year, startsOn = "2021-10-15" }, Category, "category"),
            ("no category", new { amount = "1200", every = year, startsOn = "2021-10-15" }, Category, "category"),
            ("nothing to pay", new { category = "property_tax", amount = "0", every = year, startsOn = "2021-10-15" }, Field, "amount"),
            ("a French decimal comma", new { category = "property_tax", amount = "1200,5", every = year, startsOn = "2021-10-15" }, "request.amount", "amount"),
            ("every week", new { category = "property_tax", amount = "10", every = new { count = 1, unit = "week" }, startsOn = "2021-10-15" }, Field, "every.unit"),
            ("every fortnight", new { category = "property_tax", amount = "10", every = new { count = 1, unit = "fortnight" }, startsOn = "2021-10-15" }, Field, "every.unit"),
            ("every 121 months", new { category = "property_tax", amount = "10", every = new { count = 121, unit = "month" }, startsOn = "2021-10-15" }, Field, "every.count"),
            ("no period", new { category = "property_tax", amount = "10", startsOn = "2021-10-15" }, "request.required", "every"),
            ("no start", new { category = "property_tax", amount = "10", every = year }, "request.required", "startsOn"),
            ("an end on its start", new { category = "property_tax", amount = "10", every = year, startsOn = "2021-10-15", endsOn = "2021-10-15" }, Field, "endsOn"),
            ("a start in 1899", new { category = "property_tax", amount = "10", every = year, startsOn = "1899-12-31" }, Field, "startsOn"),
        };

        foreach (var (why, body, code, path) in cases)
        {
            var (status, gotCode, gotPath) = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/recurring-costs", body));
            Assert.True((status, gotCode, gotPath) == (HttpStatusCode.UnprocessableEntity, code, path), $"{why}: got {status} {gotCode} at {gotPath}");
        }

        Assert.Empty((await PropertyAsync(app, id)).GetProperty("recurringCosts").EnumerateArray());
    }

    [Fact]
    public async Task A_cost_of_another_property_is_not_found_through_this_one_and_stays()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await NewPropertyAsync(app, Home(app));
        var other = await NewPropertyAsync(app, Home(app, name: "Studio"));
        var cost = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{other}/recurring-costs", Tax()));
        var version = Only(await PropertyAsync(app, other), "recurringCosts").GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{mine}/recurring-costs/{cost}", Tax(version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Properties}/{mine}/recurring-costs/{cost}?version={version}")).StatusCode);
        Assert.Equal(version, Only(await PropertyAsync(app, other), "recurringCosts").GetProperty("version").GetUInt32());
    }
}
