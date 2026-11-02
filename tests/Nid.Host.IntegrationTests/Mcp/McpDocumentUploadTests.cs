using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>Syndic documents design §2.3: `upload_document` files a document directly, with the rights of the upload page.</summary>
public class McpDocumentUploadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<McpClient> ClientAsync(NidApp app, string preset = TokenPresets.Propose)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static Task<CallToolResult> UploadAsync(McpClient client, ItemId item, string content, string? sourceRef = "powimo:7028:1")
        => client.CallToolAsync("upload_document", new Dictionary<string, object?>
        {
            ["contentBase64"] = content, ["filename"] = "appel.pdf", ["title"] = "Appel de fonds 01/10/2026", ["date"] = "2025-10-01",
            ["targetKind"] = TargetKinds.CoreItem, ["targetId"] = item.Value, ["role"] = DocumentRoles.CoproCall, ["sourceRef"] = sourceRef,
        }).AsTask();

    private static readonly string PdfBase64 = Convert.ToBase64String(McpDocumentToolTests.PdfOf(1));

    private static JsonElement Answer(CallToolResult result)
    {
        Assert.False(result.IsError == true, McpHarness.TextOf(result));
        return JsonDocument.Parse(McpHarness.TextOf(result)).RootElement;
    }

    private static async Task<int> CountAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(includeArchived: true, CancellationToken.None)).Count;
    }

    [Fact]
    public async Task Uploads_a_pdf_on_a_property_the_actor_writes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var answer = Answer(await UploadAsync(client, home, PdfBase64));

        Assert.False(answer.GetProperty("existing").GetBoolean());
        var detail = await JsonAsync(await GetAsync(app, app.AdultA, answer.GetProperty("id").GetGuid()));
        var document = detail.GetProperty("document");
        Assert.Equal("Appel de fonds 01/10/2026", document.GetProperty("title").GetString());
        Assert.Equal("powimo:7028:1", document.GetProperty("sourceRef").GetString());
        var link = Assert.Single(document.GetProperty("links").EnumerateArray());
        Assert.Equal(home.Value, link.GetProperty("targetId").GetGuid());
        Assert.Equal(DocumentRoles.CoproCall, link.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Second_upload_of_a_source_ref_is_existing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var first = Answer(await UploadAsync(client, home, PdfBase64));
        var second = Answer(await UploadAsync(client, home, PdfBase64));

        Assert.True(second.GetProperty("existing").GetBoolean());
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAsync(app));
    }

    [Fact]
    public async Task Refuses_a_property_the_actor_only_reads()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await app.NewSubscriptionAsync("Copro de B", app.AdultB, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var result = await UploadAsync(client, theirs, PdfBase64);

        Assert.True(result.IsError);
        Assert.Equal(0, await CountAsync(app));
    }

    [Fact]
    public async Task Refuses_a_token_without_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app, TokenPresets.Read);

        var result = await UploadAsync(client, home, PdfBase64);

        Assert.True(result.IsError);
        Assert.Contains($"'{ScopeNames.Write}'", McpHarness.TextOf(result));
        Assert.Equal(0, await CountAsync(app));
    }

    /// <summary>Review I1: only the assistant's tool files with `write`; the REST upload keeps `write:direct` for a token.</summary>
    [Fact]
    public async Task Rest_upload_with_a_propose_token_still_needs_write_direct()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var propose = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Propose);
        var direct = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.BearerContentAsync(propose.Secret, HttpMethod.Post, DocumentApi.Documents, Multipart(Pdf, "fileName=a.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await app.BearerContentAsync(direct.Secret, HttpMethod.Post, DocumentApi.Documents, Multipart(Pdf, "fileName=a.pdf"))).StatusCode);
    }

    /// <summary>Review I2: without a source reference each call files a new document, so the tool is not idempotent.</summary>
    [Fact]
    public async Task The_tool_is_a_write_that_is_not_idempotent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var client = await ClientAsync(app);

        var annotations = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(t => t.Name == "upload_document").ProtocolTool.Annotations!;

        Assert.False(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.False(annotations.IdempotentHint);
    }

    [Fact]
    public async Task Refuses_content_over_the_mcp_cap()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:McpMaxBytes"] = "1024" });
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var result = await UploadAsync(client, home, Convert.ToBase64String(McpDocumentToolTests.PdfOf(20)));

        Assert.True(result.IsError);
        Assert.Contains("1024", McpHarness.TextOf(result));
        Assert.Equal(0, await CountAsync(app));
    }

    [Fact]
    public async Task Refuses_content_that_is_not_base64()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var result = await UploadAsync(client, home, "%%% not base64 %%%");

        Assert.True(result.IsError);
        Assert.Contains("base64", McpHarness.TextOf(result));
        Assert.Equal(0, await CountAsync(app));
    }
}
