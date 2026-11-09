using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests;

/// <summary>#294: a shared item at the view level or at the edit level, set by its owners, never by an adult who may only edit it.</summary>
public class ShareLevelTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Proposals = ProposalKindHelpers.Proposals;

    private static string Sharing(ItemId item) => $"/api/v1/core/items/{item.Value}/sharing";

    private static Task<HttpResponseMessage> SetAsync(NidApp app, PersonId actor, ItemId item, string level)
        => app.RawAsync(actor, HttpMethod.Put, Sharing(item), new { shareLevel = level });

    private static Task<HttpResponseMessage> ValueAsync(NidApp app, PersonId actor, ItemId envelope, string asOf)
        => app.RawAsync(actor, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf, amount = new { amount = "1000.00", currency = "EUR" } });

    private static async Task<JsonElement> JsonAsync(NidApp app, PersonId actor, string url)
        => JsonDocument.Parse(await app.GetRawAsync(actor, url)).RootElement;

    /// <summary>Whether the proposal's detail lets this adult approve or reject it (« À valider »).</summary>
    private static async Task<bool> DecidesAsync(NidApp app, PersonId actor, Guid proposal)
        => (await JsonAsync(app, actor, $"{Proposals}/{proposal}")).GetProperty("canDecide").GetBoolean();

    private static async Task<Guid> ProposeAsync(NidApp app, PersonId actor, ItemId envelope, string asOf)
    {
        var response = await app.RawAsync(actor, HttpMethod.Post, Proposals, new
        {
            kind = "investments.record_valuation", targetItemId = envelope.Value,
            payload = new { asOf, amount = new { amount = "1200.00", currency = "EUR" } }, sourceLabel = "Relevé",
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_owner_opens_a_shared_item_to_edits_and_closes_it_again_the_other_adult_never_owns_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        // View only, today's sharing: B reads, B does not write, B decides nothing.
        var view = await JsonAsync(app, app.AdultB, Sharing(envelope));
        Assert.Equal(("shared", "view", false, false), (view.GetProperty("visibility").GetString(), view.GetProperty("shareLevel").GetString(),
            view.GetProperty("canChange").GetBoolean(), view.GetProperty("isOwner").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await ValueAsync(app, app.AdultB, envelope, "2025-01-31")).StatusCode);
        var byA = await ProposeAsync(app, app.AdultA, envelope, "2025-02-28");
        Assert.False(await DecidesAsync(app, app.AdultB, byA));

        // A opens it to edits: B writes and decides, without being able to change the level.
        var opened = await SetAsync(app, app.AdultA, envelope, "edit");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var byOwner = await JsonAsync(app, app.AdultA, Sharing(envelope));
        Assert.True(byOwner.GetProperty("canChange").GetBoolean() && byOwner.GetProperty("isOwner").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(app, app.AdultB, envelope, "view")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ValueAsync(app, app.AdultB, envelope, "2025-01-31")).StatusCode);
        Assert.True(await DecidesAsync(app, app.AdultB, byA));
        var byB = await ProposeAsync(app, app.AdultB, envelope, "2025-03-31");
        Assert.True(await DecidesAsync(app, app.AdultA, byB));

        // Back to view only: B's write access goes at once, B's proposal stays pending for A.
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(app, app.AdultA, envelope, "view")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ValueAsync(app, app.AdultB, envelope, "2025-04-30")).StatusCode);
        Assert.True(await DecidesAsync(app, app.AdultA, byB));
        Assert.False(await DecidesAsync(app, app.AdultB, byB));
        Assert.Equal("pending", (await JsonAsync(app, app.AdultA, $"{Proposals}/{byB}")).GetProperty("proposal").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_private_item_has_no_level_and_an_item_is_created_at_the_level_asked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);

        var refused = await SetAsync(app, app.AdultA, hidden, "edit");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains(ItemCodes.PrivateHasNoLevel, await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetAsync(app, app.AdultA, hidden, "admin")).StatusCode);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "Compte joint", kind = "savings", currency = "EUR", visibility = "shared", shareLevel = "edit",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        var id = new ItemId(JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid());
        Assert.Equal("edit", (await JsonAsync(app, app.AdultB, Sharing(id))).GetProperty("shareLevel").GetString());
        Assert.Equal(HttpStatusCode.Created, (await ValueAsync(app, app.AdultB, id, "2025-01-31")).StatusCode);
    }

    [Fact]
    public async Task A_childminder_arrangement_is_created_at_the_level_asked_and_the_other_adult_then_edits_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/childcare", new
        {
            name = "Garde de Léa", childId = app.Dependent.Value, carer = new { name = "Martine", pajemploi = (string?)null, approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "4", lunchPrice = (string?)null,
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { new { day = 1, arrival = "08:00", departure = "17:30", lunch = false } } } },
            },
            gaps = Array.Empty<object>(), visibility = "shared", shareLevel = "edit", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = new ItemId(JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid());

        Assert.Equal("edit", (await JsonAsync(app, app.AdultB, Sharing(id))).GetProperty("shareLevel").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/childcare/{id.Value}/close", new { on = "2026-06-30" })).StatusCode);
    }
}
