using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Childcare decision 27: the assistant reads arrangements and statements as their routes answer them.</summary>
public class McpChildcareToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<string> CreateAsync(NidApp app)
    {
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/childcare", new
        {
            name = "Garde de Léa", childId = app.Dependent.Value, carer = new { name = "Martine", pajemploi = (string?)null, approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "4", lunchPrice = (string?)null,
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { new { day = 1, arrival = "08:00", departure = "17:30", lunch = false } } } },
            },
            gaps = Array.Empty<object>(), visibility = "shared", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid().ToString();
    }

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static void SameJson(string rest, string mcp)
        => Assert.Equal(JsonDocument.Parse(rest).RootElement.ToString(), JsonDocument.Parse(mcp).RootElement.ToString());

    [Fact]
    public async Task Childcare_reads_answer_as_their_routes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        await using var client = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret);

        SameJson(await app.GetRawAsync(app.AdultA, "/api/v1/childcare?scope=household&closed=false"), (await CallAsync(client, "list_childcare", new { scope = "household" })).Text);
        var detail = (await CallAsync(client, "get_childcare", new { id })).Text;
        SameJson(await app.GetRawAsync(app.AdultA, $"/api/v1/childcare/{id}"), detail);
        Assert.Equal("Semaine type", JsonDocument.Parse(detail).RootElement.GetProperty("terms")[0].GetProperty("weekTypes")[0].GetProperty("name").GetString());
        SameJson(await app.GetRawAsync(app.AdultA, $"/api/v1/childcare/{id}/statements"), (await CallAsync(client, "get_childcare_statements", new { id })).Text);
        SameJson(await app.GetRawAsync(app.AdultA, $"/api/v1/childcare/{id}/statements/2025-09"),
            (await CallAsync(client, "get_childcare_statement", new { id, month = "2025-09" })).Text);
        SameJson(await app.GetRawAsync(app.AdultA, $"/api/v1/childcare/{id}/summary?year=2025"), (await CallAsync(client, "get_childcare_year", new { id, year = 2025 })).Text);
    }

    [Fact]
    public async Task A_malformed_month_is_an_error()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        await using var client = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret);
        Assert.True((await CallAsync(client, "get_childcare_statement", new { id, month = "septembre" })).Error);
    }
}
