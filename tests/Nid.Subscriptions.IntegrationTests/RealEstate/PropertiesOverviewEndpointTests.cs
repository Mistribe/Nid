using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Properties dashboard spec §3.4: <c>GET /properties/overview</c>, its scope, its span, and another adult's private items.</summary>
public class PropertiesOverviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Overview = $"{Properties}/overview";

    /// <summary>README §12's purchase, estimated 290 000 on 2025-06-01.</summary>
    private static async Task<Guid> EstimatedAsync(NidApp app, Dictionary<string, object?> body, PersonId? actor = null)
    {
        var id = await NewPropertyAsync(app, body, actor);
        await CreatedAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf = "2025-06-01", amount = "290000" }));
        return id;
    }

    private static Dictionary<string, object?> LinkedTo(Guid property, Dictionary<string, object?> loan)
    {
        loan["propertyId"] = property;
        return loan;
    }

    [Fact]
    public async Task The_overview_adds_up_the_properties_and_their_linked_loans()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await EstimatedAsync(app, Home(app));
        await NewAsync(app, LinkedTo(home, Golden(app)));  // 162 253.12 owed at its 60th payment
        await NewAsync(app, Short(app));                   // linked to no property: not on this page (D3)

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=household");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("EUR", 1, 1), (S(body, "currency"), body.GetProperty("count").GetInt32(), body.GetProperty("loanCount").GetInt32()));
        Assert.Equal(("290000.00", "162253.12", "127746.88"),
            (S(body.GetProperty("value"), "amount"), S(body.GetProperty("linkedBalance"), "amount"), S(body.GetProperty("netValue"), "amount")));
        Assert.Equal(("269000.00", "21000.00"), (S(body.GetProperty("costPrice"), "amount"), S(body.GetProperty("latentGain"), "amount")));
        var row = Assert.Single(body.GetProperty("properties").EnumerateArray());
        Assert.Equal((home, "primary_residence", "127746.88"), (row.GetProperty("id").GetGuid(), S(row, "type"), S(row, "netValue")));
        Assert.Equal("primary_residence", S(Assert.Single(body.GetProperty("byType").EnumerateArray()), "type"));
        var share = Assert.Single(body.GetProperty("byPerson").EnumerateArray());
        Assert.Equal((app.AdultA.Value, "127746.88"), (share.GetProperty("personId").GetGuid(), S(share.GetProperty("netValue"), "amount")));
        // Three years by default: 36 month ends and today; the last point is today's figures.
        var points = body.GetProperty("points");
        Assert.Equal(37, points.GetArrayLength());
        Assert.Equal("127746.88", S(points[36].GetProperty("netValue"), "amount"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rentReceived").ValueKind);
        Assert.Empty(body.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task A_private_loan_is_deducted_only_in_me_and_named_in_the_household()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await EstimatedAsync(app, Home(app));
        var hidden = await NewAsync(app, LinkedTo(home, Golden(app, visibility: "private")));

        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=household"));
        var mine = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=me"));

        Assert.Equal(("0.00", "290000.00"), (S(household.GetProperty("linkedBalance"), "amount"), S(household.GetProperty("netValue"), "amount")));
        var mismatch = Assert.Single(household.GetProperty("watch").EnumerateArray(), w => S(w, "code") == "visibility_mismatch");
        Assert.Equal(hidden, mismatch.GetProperty("loanId").GetGuid());
        Assert.Equal("162253.12", S(mine.GetProperty("linkedBalance"), "amount"));
    }

    [Fact]
    public async Task Another_adults_private_property_and_loan_are_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await EstimatedAsync(app, Home(app, "Studio de B", "private", app.AdultB), app.AdultB);
        var loan = await NewAsync(app, LinkedTo(theirs, Golden(app, "Prêt de B", "private", app.AdultB)), app.AdultB);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=me"));

        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.DoesNotContain(theirs.ToString(), body.ToString());
        Assert.DoesNotContain(loan.ToString(), body.ToString());
        Assert.DoesNotContain(body.GetProperty("points").EnumerateArray(), p => p.GetProperty("value").ValueKind != JsonValueKind.Null);
    }

    [Theory]
    [InlineData("12m", 13)]
    [InlineData("3y", 37)]
    public async Task The_span_sets_the_points_and_an_unknown_one_is_refused(string span, int count)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await EstimatedAsync(app, Home(app));

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?span={span}"));
        Assert.Equal(count, body.GetProperty("points").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?span=5y")).StatusCode);
    }
}
