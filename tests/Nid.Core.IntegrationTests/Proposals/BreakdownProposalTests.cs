using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>Holdings exposure spec C5: a holding's breakdown copied from its factsheet, previewed next to the current one.</summary>
public class BreakdownProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "investments.record_breakdown";
    private const string Isin = "FR0000120271";
    private static readonly Guid Doc = Guid.CreateVersion7();

    private static object[] Parts(params (string Code, decimal Pct)[] parts) => [.. parts.Select(p => new { code = p.Code, pct = p.Pct })];

    private static object Body(Guid envelope, string isin = Isin, object? regions = null, string asOf = "2025-09-30")
        => new
        {
            kind = Kind, targetItemId = envelope, sourceLabel = "Fiche Fonds A",
            payload = new { isin, asOf, regions = regions ?? Parts(("north_america", 62.5m), ("europe_ex_france", 37.5m)) },
        };

    private static async Task<Guid> HoldingAsync(NidApp app, Guid envelope, string name, string kind, string? isin)
        => (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/holdings", new { name, kind, isin })))
            .GetProperty("id").GetGuid();

    private static async Task<JsonElement> RefusedAsync(NidApp app, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalKindHelpers.Proposals, body);
        Assert.False(response.IsSuccessStatusCode);
        return await JsonOf(response);
    }

    [Fact]
    public async Task A_breakdown_proposal_is_previewed_next_to_the_current_one_and_approval_records_it_with_its_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var holding = await HoldingAsync(app, envelope, "Fonds A", "etf", Isin);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/investments/envelopes/{envelope}/holdings/{holding}/breakdown",
            new { asOf = "2025-01-15", regions = Parts(("north_america", 100m)) })).EnsureSuccessStatusCode();

        var submitted = await app.InvokeAsync(app.AdultA, new SubmitProposalCommand(Kind, new ItemId(envelope),
            JsonSerializer.SerializeToElement(((dynamic)Body(envelope)).payload), "Fiche Fonds A", Doc), TestContext.Current.CancellationToken);
        Assert.Null(submitted.Failure);
        var proposal = ((SubmittedProposal)submitted.Response!).Proposal!;
        Assert.Equal($"{Isin}:2025-09-30", proposal.SubjectKey);

        var view = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalKindHelpers.Proposals}/{proposal.Id}"));
        var preview = view.GetProperty("preview");
        Assert.Equal("Fonds A", preview.GetProperty("holdingName").GetString());
        Assert.Equal(62.5m, preview.GetProperty("proposed").GetProperty("regions")[0].GetProperty("pct").GetDecimal());
        Assert.Equal("2025-01-15", preview.GetProperty("current").GetProperty("asOf").GetString());

        var approved = (ProposalView)(await app.InvokeAsync(app.AdultA, new ApproveProposalCommand(proposal.Id, proposal.Version, null), TestContext.Current.CancellationToken)).Response!;
        Assert.Single(approved.AppliedRefs);
        await using var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        var row = await db.SecurityBreakdowns.SingleAsync(b => b.Id == approved.AppliedRefs[0], TestContext.Current.CancellationToken);
        Assert.Equal((Isin, Doc), (row.Isin, row.DocumentId));
    }

    [Fact]
    public async Task An_isin_the_envelope_does_not_hold_is_refused_at_payload_isin()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await HoldingAsync(app, envelope, "Fonds A", "etf", Isin);

        var error = (await RefusedAsync(app, Body(envelope, isin: "FR0000131104")))
            .GetProperty("errors")[0];
        Assert.Equal("payload.isin", error.GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_breakdown_that_does_not_add_up_to_100_is_refused_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await HoldingAsync(app, envelope, "Fonds A", "etf", Isin);

        var error = (await RefusedAsync(app, Body(envelope, regions: Parts(("north_america", 60m), ("france", 30m))))).GetProperty("errors")[0];
        Assert.Equal(("payload.regions", "investments.breakdown_not_100"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));

        var unknown = (await RefusedAsync(app, Body(envelope, regions: Parts(("mars", 100m))))).GetProperty("errors")[0];
        Assert.Equal("payload.regions[0].code", unknown.GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_euro_fund_isin_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await HoldingAsync(app, envelope, "Fonds euros", "euro_fund", Isin);

        var refused = await RefusedAsync(app, Body(envelope));
        var error = refused.GetProperty("errors")[0];
        Assert.Equal(("payload.isin", "investments.breakdown_not_market"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }
}
