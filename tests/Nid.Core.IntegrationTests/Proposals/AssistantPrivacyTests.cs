using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>#498, decision N (<c>docs/ai/README.md</c> §8.2): a proposal the assistant makes for a person stays theirs until
/// decided, even on a shared item: the other adult neither sees it, counts it nor hears of it, and it never merges with or
/// replaces the other adult's proposals (AST-8).</summary>
public sealed class AssistantPrivacyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "investments.record_valuation";

    private static JsonElement Valuation(string amount = "1200.00")
        => JsonSerializer.SerializeToElement(new { asOf = "2025-01-15", amount = new { amount, currency = "EUR" } });

    [Fact]
    public async Task The_other_adult_never_sees_a_pending_assistant_proposal_on_a_shared_item()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.JointEnvelopeAsync(app);

        var proposal = await SubmitAsync(app, Actor.Assistant(app.AdultA), envelope, Valuation());

        Assert.Contains(proposal.Id, await PendingAsync(app, app.AdultA));
        Assert.DoesNotContain(proposal.Id, await PendingAsync(app, app.AdultB));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/core/proposals/{proposal.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/core/proposals/{proposal.Id}")).StatusCode);
        Assert.Equal((1, 0), (await NoticesAsync(app, app.AdultA), await NoticesAsync(app, app.AdultB)));
    }

    [Fact]
    public async Task The_other_adult_cannot_decide_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.JointEnvelopeAsync(app);
        var proposal = await SubmitAsync(app, Actor.Assistant(app.AdultA), envelope, Valuation());

        var rejected = await app.InvokeAsync(app.AdultB, new RejectProposalCommand(proposal.Id, proposal.Version, null), TestContext.Current.CancellationToken);

        Assert.IsType<UnambitiousFx.Functional.Failures.NotFoundFailure>(rejected.Failure);
    }

    [Fact]
    public async Task It_neither_merges_with_nor_replaces_the_other_adults_proposals()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.JointEnvelopeAsync(app);
        var bobs = await SubmitAsync(app, Actor.Browser(app.AdultB), envelope, Valuation());

        var same = await SubmitAsync(app, Actor.Assistant(app.AdultA), envelope, Valuation());
        var newer = await SubmitAsync(app, Actor.Assistant(app.AdultA), envelope, Valuation("1300.00"));

        Assert.NotEqual(bobs.Id, same.Id); // not merged into Bob's identical proposal
        Assert.Contains(bobs.Id, await PendingAsync(app, app.AdultB)); // not superseded by the assistant's
        Assert.DoesNotContain(same.Id, await PendingAsync(app, app.AdultA)); // the author's own older one is
        Assert.Contains(newer.Id, await PendingAsync(app, app.AdultA));
    }

    [Fact]
    public async Task Once_decided_it_follows_the_items_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.JointEnvelopeAsync(app);
        var proposal = await SubmitAsync(app, Actor.Assistant(app.AdultA), envelope, Valuation());

        var approved = await app.InvokeAsync(app.AdultA, new ApproveProposalCommand(proposal.Id, proposal.Version, null), TestContext.Current.CancellationToken);

        Assert.Null(approved.Failure);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/core/proposals/{proposal.Id}")).StatusCode);
    }

    private static async Task<ProposalView> SubmitAsync(NidApp app, Actor actor, Guid envelope, JsonElement payload)
    {
        var submitted = await app.InvokeAsync(actor, new SubmitProposalCommand(Kind, new ItemId(envelope), payload, null), TestContext.Current.CancellationToken);
        Assert.Null(submitted.Failure);
        return ((SubmittedProposal)submitted.Response!).Proposal!;
    }

    private static async Task<IReadOnlyList<Guid>> PendingAsync(NidApp app, PersonId person)
        => ((ProposalList)(await app.InvokeAsync(person, new ListProposalsQuery(null, null), TestContext.Current.CancellationToken)).Response!)
            .Items.Select(p => p.Id).ToList();

    private static async Task<int> NoticesAsync(NidApp app, PersonId person)
    {
        await using var db = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await db.Notifications.AsNoTracking().CountAsync(n => n.Kind == "core.proposal_pending" && n.PersonId == person.Value, TestContext.Current.CancellationToken);
    }
}
