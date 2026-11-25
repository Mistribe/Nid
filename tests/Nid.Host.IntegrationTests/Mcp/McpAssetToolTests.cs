using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Other assets (#186): the read tools see what the person sees, the write tools replay the asset routes.</summary>
public class McpAssetToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Dictionary<string, object?> Body(NidApp app, string name, string visibility = "shared", PersonId? owner = null,
        string? purchasePrice = "1000", string? purchasedOn = "2025-01-10") => new()
    {
        ["name"] = name, ["kind"] = "art", ["purchasePrice"] = purchasePrice, ["purchasedOn"] = purchasedOn, ["visibility"] = visibility,
        ["owners"] = new[] { new { personId = (owner ?? app.AdultA).Value, shareBp = 10_000 } },
    };

    private static async Task<McpClient> ClientAsync(NidApp app, string preset)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> AnswerAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<Guid> CreateAsync(NidApp app, Dictionary<string, object?> body, PersonId actor)
    {
        var response = await app.RawAsync(actor, HttpMethod.Post, "/api/v1/assets", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task List_assets_returns_visible_assets_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await CreateAsync(app, Body(app, "Tableau"), app.AdultA);
        var theirs = await CreateAsync(app, Body(app, "Montre", "private", app.AdultB), app.AdultB);
        await using var client = await ClientAsync(app, TokenPresets.Read);

        var text = (await AnswerAsync(client, "list_assets", new { })).ToString();

        Assert.Contains(mine.ToString(), text);
        Assert.DoesNotContain(theirs.ToString(), text);
        Assert.Equal("Tableau", (await AnswerAsync(client, "get_asset", new { id = mine })).GetProperty("name").GetString());
        var (error, _) = await CallAsync(client, "get_asset", new { id = theirs });
        Assert.True(error);
    }

    [Fact]
    public async Task Create_asset_then_add_valuation_through_tools()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);

        var created = await AnswerAsync(direct, "propose_create_asset", new { body = Body(app, "Tableau"), apply = true });
        var id = created.GetProperty("appliedRefs")[0].GetGuid();
        await AnswerAsync(direct, "propose_add_asset_valuation", new { id, body = new { asOf = "2025-06-01", amount = "1500" }, apply = true });

        var asset = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/assets/{id}")).RootElement;
        Assert.Equal("Tableau", asset.GetProperty("name").GetString());
        Assert.Equal(1, asset.GetProperty("valuations").GetArrayLength());
    }

    [Fact]
    public async Task A_vehicle_cost_is_proposed_by_a_tool_and_read_back_and_a_non_vehicle_refuses_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var art = await CreateAsync(app, Body(app, "Tableau"), app.AdultA);
        var carBody = Body(app, "Citadine");
        carBody["kind"] = "vehicle";
        var car = await CreateAsync(app, carBody, app.AdultA);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        var cost = new { occurredOn = "2025-06-01", amount = "90.50", category = "service", note = "Révision" };

        var (refused, text) = await CallAsync(direct, "propose_add_asset_cost", new { id = art, body = cost, apply = true });
        Assert.True(refused);
        Assert.Contains("assets.costs_vehicle_only", text);
        await AnswerAsync(direct, "propose_add_asset_cost", new { id = car, body = cost, apply = true });

        var read = await AnswerAsync(direct, "list_asset_costs", new { id = car, year = 2025 });
        Assert.Equal("90.50", read.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("service", read.GetProperty("rows")[0].GetProperty("category").GetString());
    }

    [Fact]
    public async Task Create_asset_without_value_returns_value_required()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);

        var (error, text) = await CallAsync(direct, "propose_create_asset", new { body = Body(app, "Tableau", purchasePrice: null, purchasedOn: null), apply = true });

        Assert.True(error);
        Assert.Contains("assets.value_required", text);
    }
}
