using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Infrastructure.Audit;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests;

/// <summary>
/// #384: the sole owner of a shared item asks the other adult, who reads it, to make it private: the item stays shared until that
/// adult approves (it becomes private) or refuses (the owner is told). The direct change keeps refusing (<see cref="OwnershipEditTests"/>).
/// </summary>
public class PrivateRequestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Proposals = ProposalKindHelpers.Proposals;

    private static string Sharing(ItemId item) => $"/api/v1/core/items/{item.Value}/sharing";

    private static Task<HttpResponseMessage> AskAsync(NidApp app, PersonId actor, ItemId item)
        => app.RawAsync(actor, HttpMethod.Post, Proposals, new { kind = "core.make_private", targetItemId = item.Value, payload = new { } });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId actor, string url)
        => JsonDocument.Parse(await app.GetRawAsync(actor, url)).RootElement;

    private static Task<HttpResponseMessage> DecideAsync(NidApp app, PersonId actor, JsonElement proposal, string decision)
        => app.RawAsync(actor, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/{decision}",
            new { version = proposal.GetProperty("version").GetUInt32() });

    private static async Task<List<AuditEntryRow>> WritesAsync(NidApp app, string entity)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "write" && r.Entity == entity).OrderBy(r => r.Seq).ToListAsync();
    }

    [Fact]
    public async Task The_other_adult_approves_the_owners_request_and_the_item_becomes_private()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Compte courant", app.AdultA, Visibility.Shared);
        Assert.True((await GetAsync(app, app.AdultA, Sharing(envelope))).GetProperty("canAskPrivate").GetBoolean());
        Assert.False((await GetAsync(app, app.AdultB, Sharing(envelope))).GetProperty("canAskPrivate").GetBoolean());

        var asked = await JsonAsync(await AskAsync(app, app.AdultA, envelope));
        Assert.Equal(app.AdultA.Value, asked.GetProperty("payload").GetProperty("ownerId").GetGuid());

        // Still shared, the request shown to both; only the other adult decides it.
        foreach (var adult in new[] { app.AdultA, app.AdultB })
        {
            var sharing = await GetAsync(app, adult, Sharing(envelope));
            Assert.Equal(("shared", true), (sharing.GetProperty("visibility").GetString(), sharing.GetProperty("privateAsked").GetBoolean()));
        }
        var url = $"{Proposals}/{asked.GetProperty("id").GetGuid()}";
        Assert.False((await GetAsync(app, app.AdultA, url)).GetProperty("canDecide").GetBoolean());
        Assert.True((await GetAsync(app, app.AdultB, url)).GetProperty("canDecide").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(app, app.AdultA, asked, "approve")).StatusCode);

        var approved = await JsonAsync(await DecideAsync(app, app.AdultB, asked, "approve"));

        Assert.Equal("approved", approved.GetProperty("status").GetString());
        var after = await GetAsync(app, app.AdultA, Sharing(envelope));
        Assert.Equal(("private", false), (after.GetProperty("visibility").GetString(), after.GetProperty("privateAsked").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, Sharing(envelope))).StatusCode);

        // Audited like any ownership change: the request by A, the decision and the change by B.
        var proposal = await WritesAsync(app, "core.proposal");
        Assert.Contains(proposal, r => r is { Action: "insert" } && r.PersonId == app.AdultA.Value);
        Assert.Contains(proposal, r => r is { Action: "update" } && r.PersonId == app.AdultB.Value);
        var change = Assert.Single(await WritesAsync(app, "core.item"), r => r.Action == "update");
        Assert.Equal((app.AdultB.Value, "ApproveProposalCommand"), (change.PersonId, change.Request));
        Assert.Equal("private", JsonDocument.Parse(change.Data).RootElement.GetProperty("after").GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task The_other_adult_refuses_the_item_stays_shared_and_the_owner_is_told()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Compte courant", app.AdultA, Visibility.Shared);
        var asked = await JsonAsync(await AskAsync(app, app.AdultA, envelope));

        var rejected = await JsonAsync(await DecideAsync(app, app.AdultB, asked, "reject"));

        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        var sharing = await GetAsync(app, app.AdultB, Sharing(envelope));
        Assert.Equal(("shared", false), (sharing.GetProperty("visibility").GetString(), sharing.GetProperty("privateAsked").GetBoolean()));
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var notice = await platform.Notifications.AsNoTracking().SingleAsync(n => n.Key == $"core.private_request_refused:{asked.GetProperty("id").GetGuid()}",
            TestContext.Current.CancellationToken);
        Assert.Equal(app.AdultA.Value, notice.PersonId);
    }

    [Fact]
    public async Task Only_the_sole_adult_owner_of_a_shared_item_asks()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var together = await app.NewEnvelopeAsync("Joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var child = await app.NewEnvelopeAsync("Livret de Léa", Visibility.Shared, (app.Dependent, 10_000));
        var hidden = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);

        foreach (var item in new[] { together, child, hidden })
        {
            var refused = await AskAsync(app, app.AdultA, item);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains(ItemCodes.PrivateRequestRefused, await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }
}
