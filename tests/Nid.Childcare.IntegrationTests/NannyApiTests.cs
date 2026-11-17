using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Childcare.IntegrationTests.ChildcareApi;

namespace Nid.Childcare.IntegrationTests;

/// <summary>#94: a nanny at home, its months typed as paid, the year's totals computed by the server. Today is 2025-10-15.</summary>
public class NannyApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Nannies = "/api/v1/childcare/nannies";

    private static async Task<string> CreateNannyAsync(NidApp app, string visibility = "shared")
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Nannies, new
        {
            name = "Garde à domicile", visibility, owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return $"{Nannies}/{(await JsonAsync(response)).GetProperty("id").GetGuid()}";
    }

    private static async Task PutAsync(NidApp app, string url, string month, string paid, string cmg, string? hours = null)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/months/{month}", new { paid, cmg, hours });
        Assert.True(response.StatusCode == HttpStatusCode.OK, (await JsonAsync(response)).ToString());
    }

    [Fact]
    public async Task Months_are_recorded_corrected_and_deleted_and_the_year_is_summed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateNannyAsync(app);
        await PutAsync(app, url, "2025-01", "1000", "400", "80");
        await PutAsync(app, url, "2025-02", "1100", "400");
        await PutAsync(app, url, "2025-02", "1200", "500"); // a correction replaces the month
        await PutAsync(app, url, "2024-12", "900", "300");
        await PutAsync(app, url, "2025-03", "1300", "450");
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}/months/2025-03")).StatusCode);

        var detail = await GetAsync(app, url);

        Assert.Equal(2025, detail.GetProperty("year").GetInt32());
        Assert.Equal([2025, 2024], detail.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal(["2025-02", "2025-01"], detail.GetProperty("months").EnumerateArray().Select(m => m.GetProperty("month").GetString()));
        Assert.Equal("700.00", detail.GetProperty("months")[0].GetProperty("netCost").GetProperty("amount").GetString());
        Assert.Equal("80", detail.GetProperty("months")[1].GetProperty("hours").GetString());
        Assert.Equal("2200.00", detail.GetProperty("paid").GetProperty("amount").GetString());
        Assert.Equal("900.00", detail.GetProperty("cmg").GetProperty("amount").GetString());
        Assert.Equal("1300.00", detail.GetProperty("netCost").GetProperty("amount").GetString());
        // March to December at (600 + 700) / 2: 1300 + 10 × 650.
        var projection = detail.GetProperty("projection");
        Assert.Equal(10, projection.GetProperty("months").GetInt32());
        Assert.Equal("650.00", projection.GetProperty("mean").GetProperty("amount").GetString());
        Assert.Equal("7800.00", projection.GetProperty("total").GetProperty("amount").GetString());

        var past = await GetAsync(app, $"{url}?year=2024");
        Assert.Equal("600.00", past.GetProperty("netCost").GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, past.GetProperty("projection").ValueKind);

        var list = await GetAsync(app, $"{Nannies}?scope=household");
        var summary = list.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("1300.00", summary.GetProperty("netCost").GetProperty("amount").GetString());
        Assert.Equal(2, summary.GetProperty("months").GetInt32());
    }

    [Fact]
    public async Task A_month_ahead_a_cmg_above_the_pay_and_a_closed_nanny_are_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateNannyAsync(app);

        var ahead = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/months/2025-11", new { paid = "1000", cmg = "0" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ahead.StatusCode);
        Assert.Contains("childcare.nanny_month_in_future", (await JsonAsync(ahead)).ToString());
        var above = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/months/2025-10", new { paid = "100", cmg = "200" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, above.StatusCode);
        Assert.Contains("childcare.cmg_above_paid", (await JsonAsync(above)).ToString());

        await PutAsync(app, url, "2025-10", "1000", "300");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(app, url)).GetProperty("projection").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/months/2025-09", new { paid = "1000", cmg = "0" })).StatusCode);
        Assert.Empty((await GetAsync(app, $"{Nannies}?scope=household")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/reopen")).StatusCode);
    }

    [Fact]
    public async Task A_private_nanny_is_hidden_from_the_other_adult_and_a_shared_one_is_read_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await CreateNannyAsync(app, "private");
        var shared = await CreateNannyAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, hidden)).StatusCode);
        Assert.False((await GetAsync(app, shared, app.AdultB)).GetProperty("summary").GetProperty("writable").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"{shared}/months/2025-10", new { paid = "1000", cmg = "0" })).StatusCode);
        Assert.Single((await GetAsync(app, $"{Nannies}?scope=household", app.AdultB)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_nanny_is_deleted_with_its_months_and_stays_out_of_the_childminder_list()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateNannyAsync(app);
        await PutAsync(app, url, "2025-10", "1000", "300");
        Assert.Empty((await GetAsync(app, $"{Url}?scope=household")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{url[(url.LastIndexOf('/') + 1)..]}")).StatusCode);

        var preview = await GetAsync(app, $"{url}/deletion");
        Assert.Equal(1, preview.GetProperty("removes").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "nanny_months").GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}?version={preview.GetProperty("version").GetUInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, url)).StatusCode);
    }
}
