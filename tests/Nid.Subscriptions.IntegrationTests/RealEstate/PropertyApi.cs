using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Shared by the property endpoint tests: bodies and readers of <c>/api/v1/realestate/properties</c>.</summary>
internal static class PropertyApi
{
    public const string Properties = "/api/v1/realestate/properties";

    /// <summary>README §12's purchase: 250 000 € and 19 000 € of notary fees on 2020-06-15, owned by A unless <paramref name="owners"/> says otherwise.</summary>
    public static Dictionary<string, object?> Home(NidApp app, string name = "Maison", string visibility = "shared", params PersonId[] owners)
    {
        var who = owners.Length == 0 ? [app.AdultA] : owners;
        return new()
        {
            ["name"] = name, ["currency"] = "EUR", ["visibility"] = visibility,
            ["owners"] = who.Select(p => new { personId = p.Value, shareBp = 10_000 / who.Length }).ToArray(),
            ["type"] = "primary_residence", ["purchaseDate"] = "2020-06-15", ["purchasePrice"] = "250000", ["notaryFees"] = "19000",
        };
    }

    public static async Task<Guid> NewPropertyAsync(NidApp app, Dictionary<string, object?> body, PersonId? actor = null)
        => await LoanApi.CreatedAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, Properties, body));

    public static async Task<JsonElement> PropertyAsync(NidApp app, Guid id, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Properties}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await LoanApi.JsonAsync(response);
    }

    public static async Task<uint> VersionAsync(NidApp app, Guid id) => (await PropertyAsync(app, id)).GetProperty("version").GetUInt32();
}
