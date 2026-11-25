using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Assets.IntegrationTests;

/// <summary>What the asset write, valuation and read tests share: bodies and the few calls they all make.</summary>
internal static class AssetApi
{
    public const string Route = "/api/v1/assets";

    public static Dictionary<string, object?> Body(
        NidApp app, string name = "Tableau", string kind = "art", string? purchasePrice = "1000", string? purchasedOn = "2025-01-10",
        string? currentValue = null, string? currentValueOn = null, string visibility = "shared", PersonId? owner = null) => new()
    {
        ["name"] = name, ["kind"] = kind, ["purchasePrice"] = purchasePrice, ["purchasedOn"] = purchasedOn, ["note"] = "Un souvenir",
        ["currentValue"] = currentValue, ["currentValueOn"] = currentValueOn, ["visibility"] = visibility,
        ["owners"] = new[] { new { personId = (owner ?? app.AdultA).Value, shareBp = 10_000 } },
    };

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<ItemId> CreateAsync(NidApp app, Dictionary<string, object?> body, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, Route, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return new ItemId((await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    public static async Task<JsonElement> GetAsync(NidApp app, ItemId id, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Route}/{id.Value}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    public static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var problem = await JsonAsync(response);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        if (field is not null)
            Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.FieldPath() == field);
    }
}
