using System.Text.Json;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>« À valider » lists only the pending proposals its reader can decide; a decided one stays readable.</summary>
public sealed class UndecidableProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonElement Valuation
        = JsonSerializer.SerializeToElement(new { asOf = "2025-01-15", amount = new { amount = "1200.00", currency = "EUR" } });

    [Fact]
    public async Task A_reader_who_cannot_write_the_target_does_not_see_its_pending_proposal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA); // shared, view: B reads it, A alone writes it
        var submitted = await app.InvokeAsync(Actor.Browser(app.AdultA),
            new SubmitProposalCommand("investments.record_valuation", new ItemId(envelope), Valuation, null), TestContext.Current.CancellationToken);
        var proposal = ((SubmittedProposal)submitted.Response!).Proposal!;

        Assert.Contains(proposal.Id, await IdsAsync(app, app.AdultA, "pending"));
        Assert.DoesNotContain(proposal.Id, await IdsAsync(app, app.AdultB, "pending"));
        Assert.DoesNotContain(proposal.Id, await IdsAsync(app, app.AdultB, "pending", new ItemId(envelope)));

        Assert.Null((await app.InvokeAsync(app.AdultA, new RejectProposalCommand(proposal.Id, proposal.Version, null), TestContext.Current.CancellationToken)).Failure);
        Assert.Contains(proposal.Id, await IdsAsync(app, app.AdultB, "rejected"));
    }

    private static async Task<IReadOnlyList<Guid>> IdsAsync(NidApp app, PersonId person, string status, ItemId? target = null)
        => ((ProposalList)(await app.InvokeAsync(person, new ListProposalsQuery(status, target), TestContext.Current.CancellationToken)).Response!)
            .Items.Select(p => p.Id).ToList();
}
