using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Host.IntegrationTests.Import;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Export;

/// <summary>#94: a nanny at home and its months, exported then imported with the same amounts; imported again, unchanged or replaced.</summary>
public class NannyDataTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/childcare/nannies";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task SourceAsync(NidApp app)
    {
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            name = "Garde à domicile", visibility = "shared", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = $"{Url}/{(await JsonAsync(created)).GetProperty("id").GetGuid()}";
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/months/2025-09", new { paid = "1000.50", cmg = "400", hours = "82.5" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/months/2025-10", new { paid = "1000", cmg = "400" })).StatusCode);
    }

    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    [Fact]
    public async Task A_nanny_round_trips_and_a_changed_month_needs_replace()
    {
        await using var source = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(source, "Personne A");
        await SourceAsync(source);
        var export = await new ExportClient(source).ExportAllAsync(source.AdultA);
        var record = Assert.Single(export[NannySection]);
        Assert.Equal("""[{"month":"2025-09","paid":1000.5,"cmg":400,"hours":82.5},{"month":"2025-10","paid":1000,"cmg":400}]""", record.GetProperty("months").GetRawText());

        await using var target = await NidApp.StartAsync(fixture);
        await RoundTripTests.PrepareAsync(target, "Personne B");
        var file = RoundTripTests.AsFile(export);
        await RoundTripTests.CommitAsync(target, target.AdultA, file);

        var again = await new ExportClient(target).ExportAllAsync(target.AdultA);
        Assert.Equal(record.GetProperty("months").GetRawText(), Assert.Single(again[NannySection]).GetProperty("months").GetRawText());
        var list = await JsonAsync(await target.RawAsync(target.AdultA, HttpMethod.Get, $"{Url}?scope=household"));
        Assert.Equal("1200.50", list.GetProperty("items")[0].GetProperty("netCost").GetProperty("amount").GetString());

        // The same file again changes nothing; a corrected month is a conflict unless replaced.
        await RoundTripTests.CommitAsync(target, target.AdultA, file);
        var edited = JsonNode.Parse(file[NannySection])!.AsArray();
        edited[0]!["months"]![1]!["paid"] = 1100;
        file[NannySection] = edited.ToJsonString();
        var conflict = await new ImportClient(target).RunAsync(target.AdultA, Adults(target), file);
        Assert.Contains(conflict.Report!.Issues, i => i.Code == "import.conflict" && i.Path == $"{NannySection}[0]");
    }

    private const string NannySection = "childcare_nannies";
}
