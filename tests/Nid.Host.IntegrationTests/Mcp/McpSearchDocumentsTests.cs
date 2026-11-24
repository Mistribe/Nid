using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Domain;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#170: `search_documents` gives what the Documents page's search gives (#169), the same rights, its found words between **.</summary>
public sealed class McpSearchDocumentsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = Instant.FromUtc(2026, 11, 24, 9, 0);

    [Fact]
    public async Task Finds_what_the_api_finds_with_the_words_between_stars_and_never_another_adults_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var scan = await UploadedAsync(app, app.AdultA, Pdf, "fileName=s.pdf&title=Scan 0042&targetKind=core.household&role=invoice");
        await TextAsync(app, scan, "Intervention sur la chaudière du sous-sol");
        var mine = await UploadedAsync(app, app.AdultA, Pdf, "fileName=d.pdf&title=Devis plombier");
        await TextAsync(app, mine, "Vérifier la chaudière avant l'hiver");
        // Bob's own document, linked to nothing: private to him.
        var bobs = await UploadedAsync(app, app.AdultB, Pdf, "fileName=n.pdf&title=Note");
        await TextAsync(app, bobs, "Le code de la chaudière est 1234");
        await using var client = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret);

        var found = JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "search_documents", new { query = "chaudiere" }))).RootElement;
        var api = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/documents?scope=me&page=1&q=chaudiere"));

        Assert.Equal(Ids(api), Ids(found));
        Assert.Equal(new[] { scan, mine }.Order(), Ids(found).Order());
        Assert.DoesNotContain(bobs, Ids(found));
        Assert.DoesNotContain("1234", found.GetRawText());
        var snippet = found.GetProperty("items")[0].GetProperty("match").GetProperty("snippet").GetString()!;
        Assert.Contains("**chaudière**", snippet);
        Assert.DoesNotContain(DocumentMatch.StartMark, snippet);
        Assert.Equal("off", found.GetProperty("meaningSearch").GetString());
    }

    [Fact]
    public async Task A_search_needs_something_to_look_for_and_a_limit_from_1_to_50()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret);

        Assert.True((await CallAsync(client, "search_documents", new { query = "  " })).IsError);
        Assert.True((await CallAsync(client, "search_documents", new { query = "bail", limit = 51 })).IsError);
        var cut = JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "search_documents", new { query = "bail", limit = 1 }))).RootElement;
        Assert.Equal(0, cut.GetProperty("total").GetInt32());
    }

    private static List<Guid> Ids(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid())];

    private static Task<CallToolResult> CallAsync(McpClient client, string tool, object args)
        => client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!).AsTask();

    /// <summary>A document's text and its chunks, as the worker and the indexer would leave them (no embedding model here).</summary>
    private static async Task TextAsync(NidApp app, Guid document, string text)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IDocumentTextStore>().SaveAsync(document, [new DocumentTextPage(1, text, "text-layer")], At,
            TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IDocumentIndexStore>().SaveChunksAsync(document, Chunks.Of([(1, text)]),
            TestContext.Current.CancellationToken);
    }
}
