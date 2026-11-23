using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>#497: the assistant proposes as its person (<c>via: assistant</c>), never applies, and never decides.</summary>
public sealed class AssistantProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static JsonElement Valuation => JsonSerializer.SerializeToElement(new { asOf = "2025-01-15", amount = new { amount = "1200.00", currency = "EUR" } });

    [Fact]
    public async Task The_assistant_proposes_for_its_person_and_the_proposal_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var submitted = await app.InvokeAsync(Actor.Assistant(app.AdultA),
            new SubmitProposalCommand("investments.record_valuation", new ItemId(envelope), Valuation, null), TestContext.Current.CancellationToken);

        Assert.Null(submitted.Failure);
        var proposal = ((SubmittedProposal)submitted.Response!).Proposal!;
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await db.Proposals.AsNoTracking().SingleAsync(p => p.Id == proposal.Id, TestContext.Current.CancellationToken);
        Assert.Equal(("assistant", app.AdultA.Value), (row.CreatedVia, row.CreatedByPersonId));
    }

    [Fact]
    public async Task The_assistant_neither_applies_nor_decides()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var submitted = (SubmittedProposal)(await app.InvokeAsync(app.AdultA,
            new SubmitProposalCommand("investments.record_valuation", new ItemId(envelope), Valuation, null), TestContext.Current.CancellationToken)).Response!;

        var direct = await app.InvokeAsync(Actor.Assistant(app.AdultA),
            new ApplyDirectCommand("investments.record_valuation", new ItemId(envelope), Valuation), TestContext.Current.CancellationToken);
        var approve = await app.InvokeAsync(Actor.Assistant(app.AdultA),
            new ApproveProposalCommand(submitted.Proposal!.Id, submitted.Proposal.Version, null), TestContext.Current.CancellationToken);

        Assert.NotNull(direct.Failure); // no write:direct
        Assert.Equal(BrowserOnlyCodes.Required, approve.Failure!.Code);
    }
}
