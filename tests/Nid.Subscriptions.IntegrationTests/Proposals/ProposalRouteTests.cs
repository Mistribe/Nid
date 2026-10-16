using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>Spec 4C1 §3–§5, §7 with a test kind: the queue itself.</summary>
[Collection("postgres")]
public class ProposalRouteTests(PostgresFixture fixture)
{
    public const string Proposals = "/api/v1/core/proposals";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => NidApp.StartAsync(fixture, services => services.AddScoped<IProposalHandler, EchoProposalHandler>());

    private static object Echo(Guid target, string subject = "s1", bool refuse = false, bool applyFails = false, string? label = "Relevé")
        => new { kind = EchoProposalHandler.KindName, targetItemId = target, payload = new { subject, refuse, applyFails }, sourceLabel = label };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json));

    [Fact]
    public async Task A_propose_token_submits_and_the_owner_approves_in_the_browser()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var propose = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Propose);

        var submitted = await app.BearerAsync(propose.Secret, HttpMethod.Post, Proposals, Echo(item.Value));
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var proposal = await JsonOf(submitted);
        Assert.Equal(("pending", "api", "Relevé"), (proposal.GetProperty("status").GetString(), proposal.GetProperty("createdVia").GetString(), proposal.GetProperty("sourceLabel").GetString()));
        Assert.Equal("test.note", proposal.GetProperty("warnings")[0].GetProperty("code").GetString());
        var id = proposal.GetProperty("id").GetGuid();

        var detail = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{id}"));
        Assert.True(detail.GetProperty("canDecide").GetBoolean());
        Assert.Equal("s1", detail.GetProperty("preview").GetProperty("proposed").GetProperty("subject").GetString());

        var approved = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{id}/approve", new { version = detail.GetProperty("proposal").GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var view = await JsonOf(approved);
        Assert.Equal(("approved", "Netflix"), (view.GetProperty("status").GetString(), view.GetProperty("targetName").GetString()));
        Assert.Single(view.GetProperty("appliedRefs").EnumerateArray());
        Assert.Equal(app.AdultA.Value, view.GetProperty("decidedByPersonId").GetGuid());
    }

    [Fact]
    public async Task Submitting_twice_returns_the_same_proposal_and_another_payload_supersedes_it()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var first = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value)));
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await JsonOf(again)).GetProperty("id").GetGuid());

        var other = await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value, label: "Autre relevé"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode); // the label is provenance, not payload: same fingerprint

        var changed = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals,
            new { kind = EchoProposalHandler.KindName, targetItemId = item.Value, payload = new { subject = "s1", note = "autre" } }));
        var old = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{first.GetProperty("id").GetGuid()}"));
        Assert.Equal("superseded", old.GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal(changed.GetProperty("id").GetGuid(), old.GetProperty("proposal").GetProperty("supersededById").GetGuid());
    }

    [Fact]
    public async Task Refused_submissions_answer_their_code()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var read = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Read);

        var unknown = await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, new { kind = "nope", targetItemId = item.Value, payload = new { } });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, ProposalCodes.KindUnknown), (unknown.StatusCode, (await JsonOf(unknown)).GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value, refuse: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.BearerAsync(read.Secret, HttpMethod.Post, Proposals, Echo(item.Value))).StatusCode);
        var tooLong = await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value, label: new string('x', 201)));
        Assert.Equal("sourceLabel", (await JsonOf(tooLong)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    // Review Focus 5.
    [Fact]
    public async Task A_token_cannot_approve_even_its_own_proposal()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var direct = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Direct);
        var proposal = await JsonOf(await app.BearerAsync(direct.Secret, HttpMethod.Post, Proposals, Echo(item.Value)));
        var id = proposal.GetProperty("id").GetGuid();
        var version = proposal.GetProperty("version").GetUInt32();
        Assert.False((await JsonOf(await app.BearerAsync(direct.Secret, HttpMethod.Get, $"{Proposals}/{id}"))).GetProperty("canDecide").GetBoolean());

        foreach (var action in new[] { "approve", "reject" })
        {
            var response = await app.BearerAsync(direct.Secret, HttpMethod.Post, $"{Proposals}/{id}/{action}", new { version });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(BrowserOnlyCodes.Required, (await JsonOf(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task A_failed_application_leaves_the_proposal_pending_with_a_warning()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value, applyFails: true)));
        var id = proposal.GetProperty("id").GetGuid();

        var approved = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{id}/approve", new { version = proposal.GetProperty("version").GetUInt32() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, approved.StatusCode);
        var after = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{id}"))).GetProperty("proposal");
        Assert.Equal("pending", after.GetProperty("status").GetString());
        Assert.Contains(after.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ProposalCodes.ApplyFailed);
    }

    [Fact]
    public async Task A_throwing_application_is_rethrown_and_leaves_the_proposal_pending_with_a_warning()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals,
            new { kind = EchoProposalHandler.KindName, targetItemId = item.Value, payload = new { subject = "s1", applyThrows = true } }));
        var id = proposal.GetProperty("id").GetGuid();

        // The test server rethrows what Kestrel answers as a 500: the exception is not swallowed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{id}/approve",
            new { version = proposal.GetProperty("version").GetUInt32() }));

        var after = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{id}"))).GetProperty("proposal");
        Assert.Equal("pending", after.GetProperty("status").GetString());
        var warning = Assert.Single(after.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ProposalCodes.ApplyFailed);
        Assert.DoesNotContain("The test handler threw.", warning.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rejecting_keeps_the_note_and_a_decided_proposal_cannot_be_decided_again()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value)));
        var id = proposal.GetProperty("id").GetGuid();
        var version = proposal.GetProperty("version").GetUInt32();

        var stale = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{id}/reject", new { version = version + 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var rejected = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{id}/reject", new { version, note = "Doublon" }));
        Assert.Equal(("rejected", "Doublon", "Netflix"),
            (rejected.GetProperty("status").GetString(), rejected.GetProperty("decisionNote").GetString(), rejected.GetProperty("targetName").GetString()));
        Assert.Empty(rejected.GetProperty("appliedRefs").EnumerateArray());

        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{id}/approve", new { version = rejected.GetProperty("version").GetUInt32() });
        Assert.Equal((HttpStatusCode.Conflict, ProposalCodes.Decided), (again.StatusCode, (await JsonOf(again)).GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_blank_reject_note_is_no_note()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value)));

        var rejected = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/reject",
            new { version = proposal.GetProperty("version").GetUInt32(), note = "  " });

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonOf(rejected)).GetProperty("decisionNote").ValueKind);
    }

    [Fact]
    public async Task A_proposal_on_a_private_item_is_invisible_and_undecidable_for_the_other_adult()
    {
        await using var app = await StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("Privé", app.AdultA, Visibility.Private);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(hidden.Value)));
        var id = proposal.GetProperty("id").GetGuid();

        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultB, HttpMethod.Get, Proposals))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Proposals}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Proposals}/{id}/approve", new { version = proposal.GetProperty("version").GetUInt32() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Post, Proposals, Echo(hidden.Value))).StatusCode);
    }

    // Review Focus 2.
    [Fact]
    public async Task A_target_made_private_hides_the_proposal_from_the_other_adult()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value)));
        Assert.Single((await JsonOf(await app.RawAsync(app.AdultB, HttpMethod.Get, Proposals))).GetProperty("items").EnumerateArray());

        await MakePrivateAsync(app, item);

        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultB, HttpMethod.Get, Proposals))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}")).StatusCode);
    }

    /// <summary>No route changes an item's visibility yet: the owner's change is written to the row directly.</summary>
    private static async Task MakePrivateAsync(NidApp app, ItemId item)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        await core.Items.Where(i => i.Id == item.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Visibility, "private"));
    }

    [Fact]
    public async Task Every_view_names_its_target()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var submitted = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(item.Value)));
        Assert.Equal("Netflix", submitted.GetProperty("targetName").GetString());
        var listed = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, Proposals))).GetProperty("items")[0];
        Assert.Equal("Netflix", listed.GetProperty("targetName").GetString());
        var detail = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{submitted.GetProperty("id").GetGuid()}"));
        Assert.Equal("Netflix", detail.GetProperty("proposal").GetProperty("targetName").GetString());
    }

    [Fact]
    public async Task An_archived_envelope_keeps_its_name_on_the_views()
    {
        await using var app = await StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var submitted = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals, Echo(envelope.Value)));
        // Closing an envelope archives its item.
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/close", new { closedOn = "2025-09-30" })).EnsureSuccessStatusCode();

        var listed = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, Proposals))).GetProperty("items")[0];
        Assert.Equal("PEA", listed.GetProperty("targetName").GetString());
        var detail = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{submitted.GetProperty("id").GetGuid()}"));
        Assert.Equal("PEA", detail.GetProperty("proposal").GetProperty("targetName").GetString());
    }

    /// <summary>Spec 4D1 D3: a document that matches what Nid expects creates nothing.</summary>
    [Fact]
    public async Task A_conforme_document_creates_nothing_and_answers_no_content()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Proposals,
            new { kind = EchoProposalHandler.KindName, targetItemId = item.Value, payload = new { subject = "s1", conforme = true } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, Proposals))).GetProperty("items").EnumerateArray());
    }
}
