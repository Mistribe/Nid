using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests;

/// <summary>#25: an envelope's owners and visibility change from its settings, under the import's guards.</summary>
public class EnvelopeOwnershipTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Envelope(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static Task<HttpResponseMessage> EditAsync(NidApp app, PersonId actor, ItemId id, object body)
        => app.RawAsync(actor, HttpMethod.Patch, Envelope(id), body);

    private static object[] Owners(params (PersonId Person, int ShareBp)[] owners)
        => owners.Select(o => (object)new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray();

    private static async Task<(string Code, string Path)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("errors")[0];
        return (error.GetProperty("code").GetString()!, error.GetProperty("path").GetString()!);
    }

    [Fact]
    public async Task A_dependent_made_owner_lets_the_other_adult_edit_and_the_detail_shows_the_owners()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.Forbidden, (await EditAsync(app, app.AdultB, livret, new { note = "B" })).StatusCode);

        var given = await EditAsync(app, app.AdultA, livret, new { owners = Owners((app.Dependent, 10_000)) });
        Assert.True(given.StatusCode == HttpStatusCode.OK, await given.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultB, livret, new { note = "B" })).StatusCode);
        var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, Envelope(livret))).RootElement;
        var owner = Assert.Single(detail.GetProperty("owners").EnumerateArray());
        Assert.Equal((app.Dependent.Value, 10_000), (owner.GetProperty("personId").GetGuid(), owner.GetProperty("shareBp").GetInt32()));
        Assert.True(detail.GetProperty("canChangeOwners").GetBoolean());
    }

    [Fact]
    public async Task Removing_another_adult_from_the_owners_is_refused_on_the_owners_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await app.NewEnvelopeAsync("Compte joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));

        var refused = await EditAsync(app, app.AdultA, joint, new { owners = Owners((app.AdultA, 10_000)) });

        Assert.Equal((ItemCodes.DropsAdultOwner, "owners"), await RefusalAsync(refused));
        // Giving up one's own share stays allowed.
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultA, joint, new { owners = Owners((app.AdultB, 10_000)) })).StatusCode);
    }

    [Fact]
    public async Task Taking_read_access_away_from_another_adult_is_refused_on_the_owners_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var refused = await EditAsync(app, app.AdultA, pea, new { visibility = "private" });

        Assert.Equal((ItemCodes.TakesReadAway, "owners"), await RefusalAsync(refused));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, Envelope(pea))).StatusCode);
    }

    [Fact]
    public async Task An_adult_who_may_only_edit_does_not_change_the_owners()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/items/{pea.Value}/sharing", new { shareLevel = "edit" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await EditAsync(app, app.AdultB, pea, new { displayName = "PEA joint" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await EditAsync(app, app.AdultB, pea, new { owners = Owners((app.AdultA, 5_000), (app.AdultB, 5_000)) })).StatusCode);
        Assert.False(JsonDocument.Parse(await app.GetRawAsync(app.AdultB, Envelope(pea))).RootElement.GetProperty("canChangeOwners").GetBoolean());
    }
}
