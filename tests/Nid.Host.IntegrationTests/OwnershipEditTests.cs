using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests;

/// <summary>#25, #69, #70: an item's owners and visibility change from its edit, under the import's guards, whatever its module.</summary>
public class OwnershipEditTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public static TheoryData<string> Kinds => ["envelope", "income_source", "property"];

    /// <summary>An item of <paramref name="kind"/> owned by <paramref name="owners"/>, and its edit (and detail) route.</summary>
    private static async Task<(ItemId Id, string Url)> NewAsync(NidApp app, string kind, Visibility visibility, params (PersonId, int)[] owners)
        => kind switch
        {
            "envelope" => await app.NewEnvelopeAsync("Livret", visibility, owners) is var e ? (e, $"/api/v1/investments/envelopes/{e.Value}") : default,
            "income_source" => await app.NewIncomeSourceAsync("Salaire", visibility, owners) is var s ? (s, $"/api/v1/income/sources/{s.Value}") : default,
            "property" => await app.NewPropertyAsync("Studio", visibility, owners) is var p ? (p, $"/api/v1/realestate/properties/{p.Value}") : default,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>The edit, with the version a property's asks for (read by A, who reads the item in every case here).</summary>
    private static async Task<HttpResponseMessage> PatchAsync(NidApp app, PersonId actor, string url, object body)
    {
        var node = JsonSerializer.SerializeToNode(body)!.AsObject();
        if (url.Contains("/properties/"))
            node["version"] = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, url)).RootElement.GetProperty("version").GetUInt32();
        return await app.RawAsync(actor, HttpMethod.Patch, url, node);
    }

    private static object[] Owners(params (PersonId Person, int ShareBp)[] owners)
        => owners.Select(o => (object)new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray();

    private static async Task<(string Code, string Path)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("errors")[0];
        return (error.GetProperty("code").GetString()!, error.GetProperty("path").GetString()!);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_dependent_made_owner_lets_the_other_adult_edit_and_the_detail_shows_the_owners(string kind)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, url) = await NewAsync(app, kind, Visibility.Shared, (app.AdultA, 10_000));
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(app, app.AdultB, url, new { note = "B" })).StatusCode);

        var given = await PatchAsync(app, app.AdultA, url, new { owners = Owners((app.Dependent, 10_000)) });
        Assert.True(given.StatusCode == HttpStatusCode.OK, await given.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultB, url, new { note = "B" })).StatusCode);
        var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, url)).RootElement;
        var owner = Assert.Single(detail.GetProperty("owners").EnumerateArray());
        Assert.Equal((app.Dependent.Value, 10_000), (owner.GetProperty("personId").GetGuid(), owner.GetProperty("shareBp").GetInt32()));
        Assert.True(detail.GetProperty("canChangeOwners").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Removing_another_adult_from_the_owners_is_refused_on_the_owners_field(string kind)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, url) = await NewAsync(app, kind, Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));

        var refused = await PatchAsync(app, app.AdultA, url, new { owners = Owners((app.AdultA, 10_000)) });

        Assert.Equal((ItemCodes.DropsAdultOwner, "owners"), await RefusalAsync(refused));
        // Giving up one's own share stays allowed.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultA, url, new { owners = Owners((app.AdultB, 10_000)) })).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Taking_read_access_away_from_another_adult_is_refused_on_the_owners_field(string kind)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, url) = await NewAsync(app, kind, Visibility.Shared, (app.AdultA, 10_000));

        var refused = await PatchAsync(app, app.AdultA, url, new { visibility = "private" });

        Assert.Equal((ItemCodes.TakesReadAway, "owners"), await RefusalAsync(refused));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, url)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task An_adult_who_may_only_edit_does_not_change_the_owners(string kind)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, url) = await NewAsync(app, kind, Visibility.Shared, (app.AdultA, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/items/{id.Value}/sharing", new { shareLevel = "edit" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(app, app.AdultB, url, new { note = "B" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PatchAsync(app, app.AdultB, url, new { owners = Owners((app.AdultA, 5_000), (app.AdultB, 5_000)) })).StatusCode);
        Assert.False(JsonDocument.Parse(await app.GetRawAsync(app.AdultB, url)).RootElement.GetProperty("canChangeOwners").GetBoolean());
    }
}
