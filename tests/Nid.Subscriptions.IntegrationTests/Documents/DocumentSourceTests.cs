using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Mcp;
using Nid.Subscriptions.IntegrationTests.Proposals;
using Nid.TestSupport;
using NodaTime;
using static Nid.Subscriptions.IntegrationTests.Documents.DocumentEndpointTests;

namespace Nid.Subscriptions.IntegrationTests.Documents;

/// <summary>Issue #108 §3: a value or a flow shows its source document, to a reader of that document only.</summary>
public class DocumentSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Envelope(Guid id) => $"/api/v1/investments/envelopes/{id}";

    private static async Task<JsonElement> ReadAsync(NidApp app, PersonId reader, string url)
        => JsonDocument.Parse(await app.GetRawAsync(reader, url)).RootElement;

    private static Guid? DocumentOf(JsonElement fact)
        => fact.GetProperty("documentId").ValueKind == JsonValueKind.Null ? null : fact.GetProperty("documentId").GetGuid();

    /// <summary>The source of every value and flow of a detail, which must name at least one of each.</summary>
    private static Guid?[] SourcesOf(JsonElement detail)
    {
        var (values, flows) = (detail.GetProperty("valuations").EnumerateArray().ToArray(), detail.GetProperty("flows").EnumerateArray().ToArray());
        Assert.NotEmpty(values);
        Assert.NotEmpty(flows);
        return [.. values.Concat(flows).Select(DocumentOf)];
    }

    /// <summary>Stamps every value and flow of an item, as an approved proposal citing the document would.</summary>
    private static async Task StampAsync(NidApp app, Guid item, Guid document)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.NotEqual(0, await core.Valuations.Where(v => v.ItemId == item).ExecuteUpdateAsync(s => s.SetProperty(v => v.DocumentId, document)));
        Assert.NotEqual(0, await core.Flows.Where(f => f.ItemId == item).ExecuteUpdateAsync(s => s.SetProperty(f => f.DocumentId, document)));
    }

    /// <summary>A's shared envelope E, stamped with A's document linked only to A's private envelope P.</summary>
    private static async Task<(Guid Shared, Guid Document)> StampedWithAPrivateDocumentAsync(NidApp app)
    {
        var shared = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var hidden = await app.NewEnvelopeAsync("PEL", app.AdultA, Visibility.Private);
        var document = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=releve.pdf&targetKind=core.item&targetId={hidden.Value}&role=statement");
        await StampAsync(app, shared, document);
        return (shared, document);
    }

    /// <summary>A's statement citing the document, proposed through MCP and approved: the facts are stamped through the audited path.</summary>
    private static async Task ApprovedStatementAsync(NidApp app, Guid envelope, Guid document)
    {
        await using (var client = await McpHarness.ClientAsync(app, (await TokenRouteTests.CreateAsync(app, app.AdultA)).Secret))
        {
            var result = await client.CallToolAsync("propose_statement", JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(new
            {
                envelopeId = envelope, asOf = "2025-09-30", value = new { amount = "23500.00", currency = "EUR" },
                movements = new[] { new { on = "2025-09-05", kind = "contribution", amount = "150.00", label = "Versement", @ref = "R-1" } },
                documentId = document,
            }))!);
            Assert.True(result.IsError != true, McpHarness.TextOf(result));
        }
        var proposal = (await ReadAsync(app, app.AdultA, ProposalRouteTests.Proposals)).GetProperty("items")[0];
        var approved = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}/approve",
            new { version = proposal.GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task A_value_stamped_by_a_readable_document_shows_its_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var document = await UploadedAsync(app, app.AdultA, Pdf, "fileName=releve.pdf");
        await ApprovedStatementAsync(app, envelope, document);

        var detail = await ReadAsync(app, app.AdultA, Envelope(envelope));

        var values = detail.GetProperty("valuations").EnumerateArray().ToDictionary(v => v.GetProperty("asOf").GetString()!, DocumentOf);
        var flows = detail.GetProperty("flows").EnumerateArray().ToDictionary(f => f.GetProperty("occurredOn").GetString()!, DocumentOf);
        Assert.Equal(document, values["2025-09-30"]);
        Assert.Equal(document, flows["2025-09-05"]);
        Assert.Null(values["2025-01-31"]); // the opening value cites nothing
    }

    // Review Focus 1.
    [Fact]
    public async Task A_value_stamped_by_a_document_the_reader_cannot_read_shows_null()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (shared, document) = await StampedWithAPrivateDocumentAsync(app);

        var theirs = await app.GetRawAsync(app.AdultB, Envelope(shared));

        Assert.All(SourcesOf(JsonDocument.Parse(theirs).RootElement), Assert.Null);
        Assert.DoesNotContain(document.ToString(), theirs, StringComparison.Ordinal);
        Assert.All(SourcesOf(await ReadAsync(app, app.AdultA, Envelope(shared))), source => Assert.Equal(document, source));

        // The assistant reads through the same invoker.
        await using var client = await McpHarness.ClientAsync(app, (await TokenRouteTests.CreateAsync(app, app.AdultB, TokenPresets.Read)).Secret);
        var read = await client.CallToolAsync("get_envelope", new Dictionary<string, object?> { ["id"] = shared });
        Assert.True(read.IsError != true, McpHarness.TextOf(read));
        Assert.All(SourcesOf(JsonDocument.Parse(McpHarness.TextOf(read)).RootElement), Assert.Null);
    }

    // Review Focus 3: readability is the document's, not the link's.
    [Fact]
    public async Task A_document_readable_through_another_link_keeps_its_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (shared, document) = await StampedWithAPrivateDocumentAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{DocumentEndpointTests.Documents}/{document}/links",
            new { targetKind = "core.item", targetId = shared, role = "statement" })).StatusCode);

        Assert.All(SourcesOf(await ReadAsync(app, app.AdultB, Envelope(shared))), source => Assert.Equal(document, source));
    }

    [Fact]
    public async Task A_property_valuation_and_flow_carry_their_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await McpSeed.PropertyAsync(app, app.AdultA);
        var (_, valueRefused) = await app.InvokeAsync(app.AdultA, new Nid.RealEstate.Application.RecordPropertyValuationCommand(
            new ItemId(property), new LocalDate(2025, 3, 31), 250_000m, null));
        var (_, flowRefused) = await app.InvokeAsync(app.AdultA, new Nid.RealEstate.Application.RecordPropertyFlowCommand(
            new ItemId(property), new Nid.RealEstate.Application.PropertyFlowDraft(new LocalDate(2025, 3, 1), "expense", 900m, "other", null)));
        Assert.Null(valueRefused ?? flowRefused);
        var document = await UploadedAsync(app, app.AdultA, Pdf, "fileName=estimation.pdf");
        await StampAsync(app, property, document);

        Assert.All(SourcesOf(await ReadAsync(app, app.AdultA, $"/api/v1/realestate/properties/{property}")), source => Assert.Equal(document, source));
    }

    // Issue #175: the history's before/after are raw columns, `document_id` included, and a document link's key names its document.
    [Fact]
    public async Task An_items_history_hides_a_source_the_reader_cannot_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var document = await UploadedAsync(app, app.AdultA, Pdf, "fileName=releve.pdf");
        await ApprovedStatementAsync(app, envelope, document); // stamps the facts and links the document to the envelope
        // Unlinked again, the document is A's alone; the stamped facts and the removed link stay in the history.
        (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/documents/{document}/links/core.item/{envelope}")).EnsureSuccessStatusCode();
        var history = $"/api/v1/platform/audit/items/{envelope}";

        var forB = await app.GetRawAsync(app.AdultB, history);
        var forA = await app.GetRawAsync(app.AdultA, history);

        Assert.DoesNotContain(document.ToString(), forB, StringComparison.Ordinal);
        Assert.Contains("documents.document_link", forB, StringComparison.Ordinal); // the link's entries are kept, masked
        Assert.Contains(document.ToString(), forA, StringComparison.Ordinal);
    }
}
