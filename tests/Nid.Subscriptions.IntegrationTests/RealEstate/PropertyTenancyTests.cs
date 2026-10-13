using System.Net;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3C2 §2–§3: a property's tenancies — versions, refusals, no overlap under the property's row lock, the purchase date.</summary>
[Collection("postgres")]
public class PropertyTenancyTests(PostgresFixture fixture)
{
    private static object Lease(string startsOn, string? endsOn = null, uint? version = null)
        => new { label = "T2 meublé", startsOn, endsOn, rentMonthly = "900", version };

    [Fact]
    public async Task A_tenancy_is_added_corrected_and_deleted_against_its_version() // Review Focus #2
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app)); // bought 2020-06-15
        var tenancies = $"{Properties}/{id}/tenancies";

        var tenancy = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, new
        {
            label = "T2 meublé", startsOn = "2021-01-01", rentMonthly = "900", chargesMonthly = "50", deposit = "1800", note = "bail de 3 ans",
        }));
        var view = Only(await PropertyAsync(app, id), "tenancies");
        Assert.Equal((tenancy, "T2 meublé", "2021-01-01", (string?)null),
            (view.GetProperty("id").GetGuid(), S(view, "label"), S(view, "startsOn"), S(view, "endsOn")));
        Assert.Equal(("900.00", "50.00", "1800.00", "bail de 3 ans"),
            (S(view, "rentMonthly"), S(view, "chargesMonthly"), S(view, "deposit"), S(view, "note")));
        Assert.True(view.GetProperty("current").GetBoolean());
        var version = view.GetProperty("version").GetUInt32();

        // Ending it is correcting it with an end; it is never checked against itself.
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{tenancies}/{tenancy}", Lease("2021-01-01", "2025-10-01", version))).StatusCode);
        view = Only(await PropertyAsync(app, id), "tenancies");
        Assert.Equal(("2025-10-01", "0.00", (string?)null), (S(view, "endsOn"), S(view, "chargesMonthly"), S(view, "deposit")));
        Assert.False(view.GetProperty("current").GetBoolean()); // ended before the host's today

        Assert.Equal((HttpStatusCode.Conflict, "realestate.tenancy_conflict", (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{tenancies}/{tenancy}", Lease("2021-01-01", null, version))));
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{tenancies}/{tenancy}?version={version}")).StatusCode);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{tenancies}/{tenancy}", Lease("2021-01-01"))));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "request.required", "version"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{tenancies}/{tenancy}")));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, tenancies, Lease("2025-11-01"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete,
            $"{tenancies}/{tenancy}?version={view.GetProperty("version").GetUInt32()}")).StatusCode);
        Assert.Empty((await PropertyAsync(app, id)).GetProperty("tenancies").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{tenancies}/{tenancy}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Each_tenancy_refusal_answers_at_its_field_and_tenancies_never_overlap() // Review Focus #1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app)); // bought 2020-06-15
        var tenancies = $"{Properties}/{id}/tenancies";
        const string Field = "realestate.invalid_property_field";
        var cases = new (string Why, object Body, string Code, string Path)[]
        {
            ("a blank label", new { label = "  ", startsOn = "2021-01-01", rentMonthly = "900" }, Field, "label"),
            ("no label", new { startsOn = "2021-01-01", rentMonthly = "900" }, Field, "label"),
            ("a label of 101 characters", new { label = new string('x', 101), startsOn = "2021-01-01", rentMonthly = "900" }, Field, "label"),
            ("a tenancy before the purchase", new { label = "T2", startsOn = "2020-06-14", rentMonthly = "900" }, Field, "startsOn"),
            ("an end on its start", new { label = "T2", startsOn = "2021-01-01", endsOn = "2021-01-01", rentMonthly = "900" }, Field, "endsOn"),
            ("an end in 2201", new { label = "T2", startsOn = "2021-01-01", endsOn = "2201-01-01", rentMonthly = "900" }, Field, "endsOn"),
            ("no start", new { label = "T2", rentMonthly = "900" }, "request.required", "startsOn"),
            ("no rent", new { label = "T2", startsOn = "2021-01-01" }, "request.required", "rentMonthly"),
            ("a free tenancy", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "0" }, Field, "rentMonthly"),
            ("a French decimal comma", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900,5" }, "request.amount", "rentMonthly"),
            ("negative charges", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", chargesMonthly = "-1" }, Field, "chargesMonthly"),
            ("no deposit", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", deposit = "0" }, Field, "deposit"),
            ("a long note", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", note = new string('x', 2001) }, Field, "note"),
        };

        foreach (var (why, body, code, path) in cases)
        {
            var (status, gotCode, gotPath) = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, body));
            Assert.True((status, gotCode, gotPath) == (HttpStatusCode.UnprocessableEntity, code, path), $"{why}: got {status} {gotCode} at {gotPath}");
        }
        Assert.Empty((await PropertyAsync(app, id)).GetProperty("tenancies").EnumerateArray());

        // [2021-01-01, 2023-01-01), then one starting on its end: half-open, they touch without overlapping.
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2021-01-01", "2023-01-01")));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2023-01-01")));
        foreach (var (startsOn, endsOn) in new (string StartsOn, string? EndsOn)[] { ("2022-06-01", "2022-07-01"), ("2020-07-01", "2021-01-02"), ("2030-01-01", null) })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.tenancy_overlap", "startsOn"),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease(startsOn, endsOn))));
        Assert.Equal(["2021-01-01", "2023-01-01"], (await PropertyAsync(app, id)).GetProperty("tenancies").EnumerateArray().Select(t => S(t, "startsOn")));
    }

    [Fact]
    public async Task Two_overlapping_tenancies_sent_together_never_both_pass()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app)); // also fetches A's CSRF token: the two requests below only read it

        // Both open-ended, from January and from February 2021: each fits alone, not both.
        var sent = await Task.WhenAll(Enumerable.Range(1, 2).Select(month => app.RawAsync(app.AdultA, HttpMethod.Post,
            $"{Properties}/{id}/tenancies", Lease($"2021-0{month}-01"))));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity], sent.Select(r => r.StatusCode).Order());
        Assert.Single((await PropertyAsync(app, id)).GetProperty("tenancies").EnumerateArray());
    }

    [Fact]
    public async Task A_purchase_moved_after_a_tenancys_start_answers_at_purchaseDate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app)); // bought 2020-06-15
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/tenancies", Lease("2021-01-01")));
        var version = await VersionAsync(app, id);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "realestate.invalid_property_field", "purchaseDate"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, purchaseDate = "2021-01-02" })));
        Assert.Equal("2020-06-15", S(await PropertyAsync(app, id), "purchaseDate"));
        Assert.Equal(version, await VersionAsync(app, id));

        // On the tenancy's very start, the purchase passes.
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, purchaseDate = "2021-01-01" })).StatusCode);
    }

    [Fact]
    public async Task A_tenancy_of_another_property_is_not_found_through_this_one_and_stays()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await NewPropertyAsync(app, Home(app));
        var other = await NewPropertyAsync(app, Home(app, name: "Studio"));
        var tenancy = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{other}/tenancies", Lease("2021-01-01")));
        var version = Only(await PropertyAsync(app, other), "tenancies").GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{mine}/tenancies/{tenancy}", Lease("2021-01-01", "2022-01-01", version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Properties}/{mine}/tenancies/{tenancy}?version={version}")).StatusCode);
        var kept = Only(await PropertyAsync(app, other), "tenancies");
        Assert.Equal((version, (string?)null), (kept.GetProperty("version").GetUInt32(), S(kept, "endsOn")));
    }
}
