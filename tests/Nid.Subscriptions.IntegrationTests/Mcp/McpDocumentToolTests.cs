using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Host.Mcp;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Audit;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Documents.DocumentEndpointTests;

namespace Nid.Subscriptions.IntegrationTests.Mcp;

/// <summary>Tranches 2–3 design §4.2: `list_documents` and `read_document`, the rights of `GET /documents`, the bytes under a cap.</summary>
public class McpDocumentToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = Documents.DocumentEndpointTests.Documents;

    private static async Task<McpClient> ClientAsync(NidApp app)
        => await McpHarness.ClientAsync(app, (await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret);

    private static Task<CallToolResult> CallAsync(McpClient client, string tool, object args)
        => client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!).AsTask();

    private static string Json(string text) => JsonDocument.Parse(text).RootElement.ToString();

    [Fact]
    public async Task Reads_a_document_as_its_rest_detail_with_its_bytes_as_an_embedded_resource()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf, "fileName=bail.pdf&title=Bail%20sign%C3%A9");
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "read_document", new { id });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.Content.Count);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Contains("Bail signé", text.Text);
        Assert.Equal(Json(await app.GetRawAsync(app.AdultA, $"{Root}/{id}")), Json(text.Text));
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(result.Content[1]).Resource);
        Assert.Equal(Pdf, blob.DecodedData.ToArray());
        Assert.Equal(("application/pdf", $"nid://documents/{id}"), (blob.MimeType, blob.Uri));
    }

    [Fact]
    public async Task Sends_the_upload_instant_as_an_iso_string()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await using var client = await ClientAsync(app);

        var read = JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "read_document", new { id }))).RootElement;
        var list = JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "list_documents", new { scope = "me" }))).RootElement;

        Assert.Equal(UploadInstant, read.GetProperty("document").GetProperty("uploadedAt").GetString());
        Assert.Equal(UploadInstant, list.GetProperty("items")[0].GetProperty("uploadedAt").GetString());
    }

    // Review Focus 5.
    [Fact]
    public async Task A_document_over_the_cap_is_read_without_its_bytes_and_the_answer_says_why()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:McpMaxBytes"] = "10" });
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "read_document", new { id });

        Assert.NotEqual(true, result.IsError);
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        var answer = JsonDocument.Parse(McpHarness.TextOf(result)).RootElement;
        Assert.Equal("omitted: larger than 10 bytes", answer.GetProperty("content").GetString());
        Assert.Equal(id, answer.GetProperty("document").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_stored_file_shorter_than_its_size_is_a_clean_refusal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        var stored = Assert.Single(Directory.GetFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(stored, Pdf[..5]);
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "read_document", new { id });

        Assert.True(result.IsError);
        Assert.Equal($"Refused: {DocumentsCodes.ContentMissing}.", McpHarness.TextOf(result));
    }

    // Final review, finding 4: never part of a file, whichever way its size is wrong.
    [Fact]
    public async Task A_stored_file_longer_than_its_size_is_a_clean_refusal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        var stored = Assert.Single(Directory.GetFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(stored, [.. Pdf, .. "trailing"u8.ToArray()]);
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "read_document", new { id });

        Assert.True(result.IsError);
        Assert.Equal($"Refused: {DocumentsCodes.ContentMissing}.", McpHarness.TextOf(result));
    }

    // Review Focus 1.
    [Fact]
    public async Task Another_adults_private_document_answers_as_a_missing_one_and_both_calls_are_audited()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await UploadedAsync(app, app.AdultB, Pdf);
        await using var client = await ClientAsync(app);

        var hidden = await CallAsync(client, "read_document", new { id = theirs });
        var missing = await CallAsync(client, "read_document", new { id = Guid.NewGuid() });

        foreach (var result in new[] { hidden, missing })
        {
            Assert.True(result.IsError);
            Assert.Equal("Not found.", McpHarness.TextOf(result));
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        }
        Assert.Equal(2, (await SecurityEventTests.EventsAsync(app, SecurityEventNames.McpToolCalled)).Count);
    }

    [Fact]
    public async Task Lists_the_inbox_as_rest_does_without_the_other_adults_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await app.NewOwnedSubscriptionAsync("Maison", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var theirs = await app.NewSubscriptionAsync("CoffreSecretB", app.AdultB, Visibility.Private);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={joint.Value}&role=deed");
        var linked = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=b.pdf&targetKind=core.item&targetId={joint.Value}&role=deed");
        var others = await UploadedAsync(app, app.AdultB, Pdf);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Root}/{id}/links",
            new { targetKind = TargetKinds.CoreItem, targetId = theirs.Value, role = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{id}/links/{TargetKinds.CoreItem}/{joint.Value}")).StatusCode);
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "list_documents", new { inbox = true });

        Assert.NotEqual(true, result.IsError);
        var text = McpHarness.TextOf(result);
        Assert.Equal(Json(await app.GetRawAsync(app.AdultA, $"{Root}?inbox=true")), Json(text));
        var listed = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([id], listed);
        Assert.DoesNotContain(linked, listed);
        Assert.DoesNotContain(others, listed);
        Assert.DoesNotContain("CoffreSecretB", text);
        Assert.DoesNotContain(theirs.Value.ToString(), text);
    }

    [Fact]
    public async Task Lists_by_target_as_rest_does_and_refuses_a_target_id_alone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        await UploadedAsync(app, app.AdultA, Pdf);
        await using var client = await ClientAsync(app);

        var result = await CallAsync(client, "list_documents", new { scope = "me", targetKind = TargetKinds.CoreItem, targetId = shared.Value });
        Assert.Equal(Json(await app.GetRawAsync(app.AdultA, $"{Root}?scope=me&targetKind={TargetKinds.CoreItem}&targetId={shared.Value}")),
            Json(McpHarness.TextOf(result)));
        Assert.Contains(id.ToString(), McpHarness.TextOf(result));

        var alone = await CallAsync(client, "list_documents", new { targetId = shared.Value });
        Assert.True(alone.IsError);
    }

    private static Task<Guid> DatedAsync(NidApp app, string date) => UploadedAsync(app, app.AdultA, Pdf, $"fileName={date}.pdf&date={date}");

    private static Guid[] Ids(JsonElement answer) => answer.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToArray();

    // Final review, finding 5: a large household narrows the list rather than being refused whole.
    [Fact]
    public async Task Lists_one_year_by_document_date_else_upload_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var old = await DatedAsync(app, "2023-05-10");
        var recent = await DatedAsync(app, "2024-02-01");
        var undated = await UploadedAsync(app, app.AdultA, Pdf); // its upload year counts
        await using var client = await ClientAsync(app);
        int thisYear; // the app's clock, not the machine's
        await using (var scope = app.Services.CreateAsyncScope())
            thisYear = (await scope.ServiceProvider.GetRequiredService<IHouseholdCalendar>().TodayAsync(default)).Year;

        async Task<Guid[]> YearAsync(int year)
            => Ids(JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "list_documents", new { scope = "me", year }))).RootElement);

        Assert.Equal([old], await YearAsync(2023));
        Assert.Equal([recent], await YearAsync(2024));
        Assert.Contains(undated, await YearAsync(thisYear));
        Assert.DoesNotContain(old, await YearAsync(thisYear));
    }

    [Fact]
    public async Task Lists_at_most_the_limit_newest_first_and_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await DatedAsync(app, "2024-01-15");
        var second = await DatedAsync(app, "2024-03-15");
        var third = await DatedAsync(app, "2024-06-15");
        await using var client = await ClientAsync(app);

        var cut = JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "list_documents", new { scope = "me", limit = 2 }))).RootElement;
        var whole = JsonDocument.Parse(McpHarness.TextOf(await CallAsync(client, "list_documents", new { scope = "me" }))).RootElement;

        Assert.Equal([third, second], Ids(cut));
        Assert.Contains("2 of 3", cut.GetProperty("truncated").GetString());
        Assert.Equal([third, second, first], Ids(whole));
        Assert.False(whole.TryGetProperty("truncated", out _));
    }

    /// <summary>Every token holds `read` today: the tools are called as a token holding none, through the real pipeline.</summary>
    [Fact]
    public async Task A_token_without_the_read_scope_is_refused_like_the_other_read_tools()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IActorAccessor>().Current = Actor.Token(app.AdultA, Guid.NewGuid(), new HashSet<string>(), ActorOrigin.Mcp);

        var goals = await GoalTools.GetGoals(services);
        var read = await DocumentTools.ReadDocument(services, id);
        var list = await DocumentTools.ListDocuments(services);

        Assert.True(goals.IsError);
        foreach (var refused in new[] { read, list })
        {
            Assert.True(refused.IsError);
            Assert.Equal(McpHarness.TextOf(goals), McpHarness.TextOf(refused));
            Assert.Single(refused.Content);
        }
    }
}
