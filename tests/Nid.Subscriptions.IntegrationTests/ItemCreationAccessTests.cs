using System.Net;
using System.Text.Json;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>
/// <c>authorization.md</c> §5, creation: "L'acteur est l'un des propriétaires, ou tous les propriétaires
/// sont des <c>dependent</c>. On ne crée pas un élément au seul nom de l'autre adulte." Without it an adult
/// can plant an item in the other's private space — created, then invisible to its own author.
/// </summary>
[Collection("postgres")]
public class ItemCreationAccessTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_adult_cannot_create_a_private_item_owned_by_the_other()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions",
            Body("planted", "private", (app.AdultB.Value, 10_000)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Nothing was planted: B sees no such item either.
        var list = await app.GetRawAsync(app.AdultB, "/api/v1/subscriptions?scope=me");
        Assert.DoesNotContain("planted", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_adult_cannot_create_a_shared_item_owned_by_the_other_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions",
            Body("planted", "shared", (app.AdultB.Value, 10_000)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_adult_creates_an_item_they_co_own_with_the_other()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions",
            Body("together", "shared", (app.AdultA.Value, 5_000), (app.AdultB.Value, 5_000)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Either_adult_creates_an_item_owned_by_the_dependent_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var actor in new[] { app.AdultA, app.AdultB })
        {
            var response = await app.RawAsync(actor, HttpMethod.Post, "/api/v1/subscriptions",
                Body($"for-the-kid-{actor.Value}", "shared", (app.Dependent.Value, 10_000)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task An_adult_cannot_hide_behind_the_dependent_to_create_for_the_other()
    {
        await using var app = await NidApp.StartAsync(fixture);

        // Mixed owners: the exemption is "ALL owners are dependents", not "one of them is".
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions",
            Body("planted", "shared", (app.AdultB.Value, 5_000), (app.Dependent.Value, 5_000)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static object Body(string name, string visibility, params (Guid Person, int ShareBp)[] owners) => new
    {
        displayName = name,
        visibility,
        owners = owners.Select(o => new { personId = o.Person, shareBp = o.ShareBp }).ToArray(),
        autoRenew = true,
    };
}
