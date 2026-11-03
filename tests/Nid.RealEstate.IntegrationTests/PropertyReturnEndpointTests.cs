using System.Net;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Property return spec §5: the return and its assumptions over the API, the host's today being 2025-10-15.</summary>
public class PropertyReturnEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> RentalAsync(NidApp app)
    {
        var body = Home(app);
        body["type"] = "rental";
        var id = await NewPropertyAsync(app, body);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf = "2025-06-01", amount = "290000" }));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/tenancies", new { label = "T3", startsOn = "2020-09-01", rentMonthly = "900" }));
        return id;
    }

    private static string Route(Guid id, string tail = "return") => $"{Properties}/{id}/{tail}";

    private static async Task<System.Text.Json.JsonElement> ReturnAsync(NidApp app, Guid id)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, Route(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static IEnumerable<string> Defaulted(System.Text.Json.JsonElement r)
        => r.GetProperty("assumptions").GetProperty("defaulted").EnumerateArray().Select(d => d.GetString()!);

    [Fact]
    public async Task Get_without_assumptions_uses_defaults()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var r = await ReturnAsync(app, await RentalAsync(app));

        Assert.Equal("0.0100", S(r.GetProperty("assumptions"), "valueGrowth"));
        Assert.Contains("valueGrowth", Defaulted(r));
        Assert.Equal(16, r.GetProperty("sensitivity").GetArrayLength());
        Assert.Contains(r.GetProperty("warnings").EnumerateArray(), w => S(w, "code") == "no_pinel");
    }

    [Fact]
    public async Task Put_then_get()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RentalAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, Route(id, "return-assumptions"), new { yearsAfterPinel = 3 })).StatusCode);

        var r = await ReturnAsync(app, id);
        Assert.Equal(3, r.GetProperty("assumptions").GetProperty("yearsAfterPinel").GetInt32());
        Assert.DoesNotContain("yearsAfterPinel", Defaulted(r));
        Assert.Equal("2028-10-15", S(r.GetProperty("scenario").GetProperty("outcome"), "resaleOn"));
    }

    [Theory]
    [InlineData("resaleFees", "0.2")]
    [InlineData("marginalRate", "0.25")]
    [InlineData("valueGrowth", "0.11")]
    public async Task Out_of_bounds_is_refused(string field, string value)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RentalAsync(app);
        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Route(id, "return-assumptions"), new Dictionary<string, object?> { [field] = value });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reader_cannot_save_assumptions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RentalAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, Route(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, Route(id, "return-assumptions"), new { yearsAfterPinel = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, Route(id, "return-assumptions"))).StatusCode);
    }

    [Fact]
    public async Task Delete_restores_defaults()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RentalAsync(app);
        await app.RawAsync(app.AdultA, HttpMethod.Put, Route(id, "return-assumptions"), new { yearsAfterPinel = 3, resaleFees = "0.07" });
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, Route(id, "return-assumptions"))).StatusCode);

        Assert.Equal(9, Defaulted(await ReturnAsync(app, id)).Count());
    }
}
