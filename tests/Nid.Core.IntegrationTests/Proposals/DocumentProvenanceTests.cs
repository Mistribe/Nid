using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests.Proposals;

/// <summary>Documents tranche 3 §4.1: a proposal's source document, carried onto the facts it creates and kept on their corrections.</summary>
public class DocumentProvenanceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Guid DocA = Guid.CreateVersion7();
    private static readonly Guid DocB = Guid.CreateVersion7();

    private static JsonElement Statement(string value = "23500.00") => JsonSerializer.SerializeToElement(new
    {
        asOf = "2025-09-30", value = new { amount = value, currency = "EUR" },
        movements = new[] { new Dictionary<string, object?> { ["on"] = "2025-09-05", ["kind"] = "contribution", ["amount"] = "150.00", ["label"] = "Versement" } },
    });

    private static async Task<T> OkAsync<T>(Task<(object? Response, Failure? Failure)> invoked)
    {
        var (response, failure) = await invoked;
        Assert.True(failure is null, failure?.Message);
        return (T)response!;
    }

    private static Task<SubmittedProposal> SubmitAsync(NidApp app, string kind, Guid target, JsonElement payload, Guid? document)
        => OkAsync<SubmittedProposal>(app.InvokeAsync(app.AdultA, new SubmitProposalCommand(kind, new ItemId(target), payload, null, document)));

    private static Task<ProposalView> ApproveAsync(NidApp app, ProposalView proposal)
        => OkAsync<ProposalView>(app.InvokeAsync(app.AdultA, new ApproveProposalCommand(proposal.Id, proposal.Version, null)));

    /// <summary>The <c>document_id</c> of every valuation and flow row among <paramref name="ids"/>, by id.</summary>
    private static async Task<Dictionary<Guid, Guid?>> StampsAsync(NidApp app, IReadOnlyCollection<Guid> ids)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var valuations = await core.Valuations.Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.DocumentId }).ToListAsync();
        var flows = await core.Flows.Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.DocumentId }).ToListAsync();
        return valuations.Concat(flows).ToDictionary(r => r.Id, r => r.DocumentId);
    }

    [Fact]
    public async Task A_submitted_document_is_stored_and_shown_and_enters_the_fingerprint()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var first = await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocA);
        var stored = first.Proposal!;
        Assert.True(first.Created);
        Assert.Equal(DocA, stored.DocumentId);
        var detail = await OkAsync<ProposalDetail>(app.InvokeAsync(app.AdultA, new GetProposalQuery(stored.Id), TestContext.Current.CancellationToken));
        Assert.Equal(DocA, detail.Proposal.DocumentId);

        var same = await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocA);
        Assert.False(same.Created);
        Assert.Equal(stored.Id, same.Proposal!.Id);

        var other = await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocB);
        Assert.True(other.Created);
        Assert.Equal(DocB, other.Proposal!.DocumentId);
        Assert.NotEqual(stored.Id, other.Proposal!.Id);

        var none = await SubmitAsync(app, "investments.record_statement", envelope, Statement(), null);
        Assert.True(none.Created);
        Assert.Null(none.Proposal!.DocumentId);
    }

    // Review Focus 3.
    [Fact]
    public async Task Approval_stamps_the_document_on_the_value_and_the_flows_it_created()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var approved = await ApproveAsync(app, (await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocA)).Proposal!);

        Assert.Equal(2, approved.AppliedRefs.Count); // one flow, one value
        var stamps = await StampsAsync(app, approved.AppliedRefs);
        Assert.Equal(2, stamps.Count);
        Assert.All(stamps.Values, d => Assert.Equal(DocA, d));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var refs = approved.AppliedRefs.ToList();
        var others = await core.Valuations.Where(r => r.ItemId == envelope && !refs.Contains(r.Id)).Select(r => r.DocumentId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        others.AddRange(await core.Flows.Where(r => r.ItemId == envelope && !refs.Contains(r.Id)).Select(r => r.DocumentId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, others.Count); // the seeded opening and first value
        Assert.All(others, Assert.Null);

        // The stamp is a write like any other: audited under the envelope.
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var updates = await platform.AuditLog.Where(r => r.Category == "write" && r.Action == "update" && r.Entity == "core.valuation").ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(updates, e => e.ItemIds.SequenceEqual([envelope]) && e.Data.Contains("document_id") && e.Data.Contains(DocA.ToString()));
    }

    // Review Focus 4: re-recording a value on its date is how a value is corrected (core-data-model.md §5.4, « Corriger la valeur »).
    [Fact]
    public async Task A_value_re_recorded_on_its_date_keeps_its_document_unless_a_new_source_replaces_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await ApproveAsync(app, (await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocA)).Proposal!);
        var asOf = new LocalDate(2025, 9, 30);

        var corrected = await OkAsync<Guid>(app.InvokeAsync(app.AdultA,
            new RecordValuationCommand(new ItemId(envelope), asOf, Money.Create(23600m, Currency.Eur).Ok(), null), TestContext.Current.CancellationToken));
        Assert.Equal(DocA, (await StampsAsync(app, [corrected]))[corrected]);

        var replaced = await ApproveAsync(app, (await SubmitAsync(app, "investments.record_statement", envelope, Statement("24000.00"), DocB)).Proposal!);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var current = await core.Valuations.SingleAsync(r => r.ItemId == envelope && r.AsOf == asOf && r.RetiredAt == null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(current.Id, replaced.AppliedRefs);
        Assert.Equal(DocB, current.DocumentId);
    }

    private static JsonElement Holdings() => JsonSerializer.SerializeToElement(new
    {
        asOf = "2025-09-30", recordValuation = true, movements = Array.Empty<object>(),
        lines = new[] { new { name = "ETF Monde", kind = "etf", units = "235", unitPrice = "100", value = "23500.00" } },
    });

    /// <summary>The live value of the envelope on 2025-09-30.</summary>
    private static async Task<(Guid Id, Guid? DocumentId)> ValueOnAsync(NidApp app, Guid envelope)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Valuations.SingleAsync(r => r.ItemId == envelope && r.AsOf == new LocalDate(2025, 9, 30) && r.RetiredAt == null);
        return (row.Id, row.DocumentId);
    }

    // Final review, finding 1: the value a holdings snapshot records is one of the facts it applies.
    [Fact]
    public async Task A_holdings_snapshot_recording_its_value_stamps_that_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var approved = await ApproveAsync(app, (await SubmitAsync(app, "investments.record_holdings_snapshot", envelope, Holdings(), DocA)).Proposal!);

        var value = await ValueOnAsync(app, envelope);
        Assert.Contains(value.Id, approved.AppliedRefs);
        Assert.Equal(DocA, value.DocumentId);
    }

    [Fact]
    public async Task A_holdings_snapshot_recording_its_value_replaces_the_source_of_a_value_on_that_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await ApproveAsync(app, (await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocB)).Proposal!);
        Assert.Equal(DocB, (await ValueOnAsync(app, envelope)).DocumentId);

        await ApproveAsync(app, (await SubmitAsync(app, "investments.record_holdings_snapshot", envelope, Holdings(), DocA)).Proposal!);

        Assert.Equal(DocA, (await ValueOnAsync(app, envelope)).DocumentId);
    }

    [Fact]
    public async Task A_kind_writing_no_fact_is_approved_and_stamps_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA);
        var payslip = JsonSerializer.SerializeToElement(new { paidOn = "2025-09-28", net = new { amount = "2400.00", currency = "EUR" } });

        var approved = await ApproveAsync(app, (await SubmitAsync(app, "income.change_pay", source, payslip, DocA)).Proposal!);

        Assert.Equal("approved", approved.Status);
        Assert.NotEmpty(approved.AppliedRefs);
        Assert.Empty(await StampsAsync(app, approved.AppliedRefs));
        Assert.Equal(DocA, approved.DocumentId);
    }

    [Fact]
    public async Task A_direct_write_stamps_the_same_way()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var written = await DirectWriteTests.AsTokenAsync(app, DirectWriteTests.Direct,
            new ApplyDirectCommand("investments.record_statement", new ItemId(envelope), Statement(), DocA));

        Assert.True(written.TryGetValue(out var write), written.TryGetFailure(out var f) ? f!.Message : null);
        Assert.Equal(2, write!.AppliedRefs.Count);
        var stamps = await StampsAsync(app, write.AppliedRefs);
        Assert.Equal(2, stamps.Count);
        Assert.All(stamps.Values, d => Assert.Equal(DocA, d));
    }

    // Review Focus 4.
    [Fact]
    public async Task A_corrected_value_and_flow_keep_their_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var approved = await ApproveAsync(app, (await SubmitAsync(app, "investments.record_statement", envelope, Statement(), DocA)).Proposal!);
        Guid valuation, flow;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            valuation = await core.Valuations.Where(r => approved.AppliedRefs.Contains(r.Id)).Select(r => r.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            flow = await core.Flows.Where(r => approved.AppliedRefs.Contains(r.Id)).Select(r => r.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var item = new ItemId(envelope);
        var eur = (decimal amount) => Money.Create(amount, Currency.Eur).Ok();
        Valuation newValue;
        Flow newFlow;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            newValue = (await scope.ServiceProvider.GetRequiredService<IValuations>()
                .SupersedeAsync(item, valuation, new NewValuation(item, new LocalDate(2025, 9, 30), eur(23600m), null), TestContext.Current.CancellationToken)).Ok();
            newFlow = (await scope.ServiceProvider.GetRequiredService<IFlows>()
                .SupersedeAsync(item, flow, new NewFlow(item, new LocalDate(2025, 9, 5), FlowKind.Contribution, eur(151m), null, null, null), TestContext.Current.CancellationToken)).Ok();
        }

        var stamps = await StampsAsync(app, [newValue.Id, newFlow.Id]);
        Assert.Equal(DocA, stamps[newValue.Id]);
        Assert.Equal(DocA, stamps[newFlow.Id]);
    }
}
