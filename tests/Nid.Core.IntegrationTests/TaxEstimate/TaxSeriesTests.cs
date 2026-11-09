using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>The explorer's tax series: each notice's net tax, dated 1 January, for the notices the reader holds.</summary>
public class TaxSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task AddAsync(NidApp app, int year, string? netTax, params PersonId[] declarants)
    {
        var body = new Dictionary<string, object?>
        {
            ["year"] = year,
            ["declarants"] = declarants.Select((p, i) => new { personId = p.Value, rank = i + 1 }).ToArray(),
        };
        if (netTax is not null) body["netTax"] = new { amount = netTax, currency = "EUR" };
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(declarants[0], HttpMethod.Post, "/api/v1/tax/assessments", body)).StatusCode);
    }

    private static (string, decimal)[] Points(JsonElement view) => view.GetProperty("series")[0].GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))).ToArray();

    [Fact]
    public async Task Catalogue_lists_the_tax_series_for_a_member()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var items = (await GetAsync(app, app.AdultB, "/api/v1/series?q=tax")).GetProperty("items").EnumerateArray().ToArray();

        var item = Assert.Single(items);
        Assert.Equal(("tax:yearly", "tax_yearly", "currency:EUR", JsonValueKind.Null),
            (item.GetProperty("key").GetString(), item.GetProperty("kind").GetString(), item.GetProperty("unit").GetString(), item.GetProperty("item").ValueKind));
    }

    [Fact]
    public async Task The_tax_series_is_each_notices_net_tax()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, 2022, null, app.AdultA);
        await AddAsync(app, 2023, "1000.00", app.AdultA);
        await AddAsync(app, 2024, "1200.00", app.AdultA);

        var points = Points(await GetAsync(app, app.AdultA, "/api/v1/series/points?s=tax:yearly&scope=me")); // a solo notice is not a household one

        Assert.Equal([("2023-01-01", 1000m), ("2024-01-01", 1200m)], points);
        var screen = (await GetAsync(app, app.AdultA, "/api/v1/tax/assessments?scope=me")).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("netTax").ValueKind != JsonValueKind.Null).OrderBy(n => n.GetProperty("year").GetInt32())
            .Select(n => ($"{n.GetProperty("year").GetInt32()}-01-01", decimal.Parse(n.GetProperty("netTax").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture)));
        Assert.Equal(screen, points);
    }

    [Fact]
    public async Task Another_adults_notice_stays_out_and_a_joint_one_counts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, 2023, "900.00", app.AdultA);
        await AddAsync(app, 2024, "1100.00", app.AdultA, app.AdultB);

        var asB = await GetAsync(app, app.AdultB, "/api/v1/series/points?s=tax:yearly");

        Assert.Equal([("2024-01-01", 1100m)], Points(asB));
    }
}
