using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Documents.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#305: the assistant files documents through proposals, and uploads a file without its bytes passing through the model.</summary>
public class McpDocumentFilingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<McpClient> ClientAsync(NidApp app, string preset = TokenPresets.Propose)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> AnswerAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task ApproveAsync(NidApp app, JsonElement proposed)
    {
        var id = proposed.GetProperty("proposal").GetProperty("id").GetGuid();
        var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{ProposalKindHelpers.Proposals}/{id}")).RootElement;
        var approved = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalKindHelpers.Proposals}/{id}/approve",
            new { version = detail.GetProperty("proposal").GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    private static async Task<JsonElement> DetailAsync(NidApp app, Guid id) => await JsonAsync(await GetAsync(app, app.AdultA, id));

    private static async Task<int> CountAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(includeArchived: true, CancellationToken.None)).Count;
    }

    private static async Task<int> PendingAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items").GetArrayLength();

    private static async Task<Guid> LinkedAsync(NidApp app, ItemId item, string note = "note=Gardée")
        => await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&title=Sans date&targetKind=core.item&targetId={item.Value}&role=other&{note}");

    [Fact]
    public async Task One_document_is_edited_and_a_link_role_set_once_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var document = await LinkedAsync(app, item);
        await using var client = await ClientAsync(app);

        var edit = await AnswerAsync(client, "propose_edit_document", new { id = document, body = new { title = "Relevé mars", documentDate = "2024-03-31", note = "Gardée" } });
        var role = await AnswerAsync(client, "propose_set_document_role", new { id = document, targetKind = TargetKinds.CoreItem, targetId = item.Value, body = new { role = DocumentRoles.Statement } });

        Assert.Equal("documents.edit_document", edit.GetProperty("proposal").GetProperty("kind").GetString());
        Assert.Equal("Sans date", (await DetailAsync(app, document)).GetProperty("document").GetProperty("title").GetString());

        await ApproveAsync(app, edit);
        await ApproveAsync(app, role);
        var detail = await DetailAsync(app, document);
        Assert.Equal(("Relevé mars", "2024-03-31"), (detail.GetProperty("document").GetProperty("title").GetString(), detail.GetProperty("document").GetProperty("documentDate").GetString()));
        Assert.Equal(DocumentRoles.Statement, detail.GetProperty("document").GetProperty("links")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_filing_round_is_one_proposal_approved_in_one_action()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var documents = new List<Guid>();
        for (var i = 0; i < 3; i++)
            documents.Add(await LinkedAsync(app, item));
        await using var client = await ClientAsync(app);

        var proposed = await AnswerAsync(client, "propose_file_documents", new
        {
            sourceLabel = "Classement PEA",
            body = new
            {
                edits = documents.Select((id, i) => new { id, documentDate = $"2024-0{i + 1}-28" }),
                roles = documents.Select(id => new { id, targetKind = TargetKinds.CoreItem, targetId = item.Value, role = DocumentRoles.Statement }),
            },
        });

        Assert.Equal(1, await PendingAsync(app));
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, documents[0])).GetProperty("document").GetProperty("documentDate").ValueKind);

        await ApproveAsync(app, proposed);
        Assert.Equal(0, await PendingAsync(app));
        for (var i = 0; i < 3; i++)
        {
            var detail = await DetailAsync(app, documents[i]);
            Assert.Equal($"2024-0{i + 1}-28", detail.GetProperty("document").GetProperty("documentDate").GetString());
            Assert.Equal(DocumentRoles.Statement, detail.GetProperty("document").GetProperty("links")[0].GetProperty("role").GetString());
            // What the line does not give stays.
            Assert.Equal(("Sans date", "Gardée"), (detail.GetProperty("document").GetProperty("title").GetString(), detail.GetProperty("note").GetString()));
        }
    }

    [Fact]
    public async Task A_refused_line_is_named_and_nothing_is_proposed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var document = await LinkedAsync(app, item);
        var unreadable = await UploadedAsync(app, app.AdultB, Pdf);
        await using var client = await ClientAsync(app);

        async Task RefusedAsync(object body, string named)
        {
            var (error, text) = await CallAsync(client, "propose_file_documents", new { body });
            Assert.True(error, text);
            Assert.Contains(named, text);
        }

        await RefusedAsync(new { edits = new object[] { new { id = document, documentDate = "2024-01-31" }, new { id = document, documentDate = "31/01/2024" } } }, "payload.body.edits[1].documentDate");
        await RefusedAsync(new { edits = new object[] { new { id = document, documentDate = "2999-01-31" } } }, "payload.body.edits[0].documentDate");
        await RefusedAsync(new { edits = new object[] { new { id = document, title = "A" }, new { id = unreadable, title = "B" } } }, "Not found.");
        await RefusedAsync(new { roles = new object[] { new { id = document, targetKind = TargetKinds.CoreItem, targetId = item.Value, role = "nope" } } }, "payload.body.roles[0].role");
        await RefusedAsync(new { }, "documents.invalid_field");

        Assert.Equal(0, await PendingAsync(app));
        Assert.Equal("Sans date", (await DetailAsync(app, document)).GetProperty("document").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_round_applied_directly_is_all_or_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var document = await LinkedAsync(app, item);
        await using var client = await ClientAsync(app, TokenPresets.Direct);

        var (error, _) = await CallAsync(client, "propose_file_documents", new
        {
            apply = true, body = new { edits = new object[] { new { id = document, title = "Écrit" }, new { id = Guid.NewGuid(), title = "Absent" } } },
        });

        Assert.True(error);
        Assert.Equal("Sans date", (await DetailAsync(app, document)).GetProperty("document").GetProperty("title").GetString());
    }

    private static object UploadArguments(ItemId item, string role = DocumentRoles.Statement, string date = "2024-03-31", string? sourceRef = null)
        => new { filename = "releve.pdf", title = "Relevé mars", date, targetKind = TargetKinds.CoreItem, targetId = item.Value, role, sourceRef };

    private static async Task<string> UrlAsync(McpClient client, ItemId item, string? sourceRef = null)
        => (await AnswerAsync(client, "request_document_upload", UploadArguments(item, sourceRef: sourceRef))).GetProperty("url").GetString()!;

    private static Task<HttpResponseMessage> PostAsync(NidApp app, string url, byte[] body)
        => app.Server.CreateClient().PostAsync(new Uri(url).PathAndQuery, new ByteArrayContent(body), TestContext.Current.CancellationToken);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    [Fact]
    public async Task A_25_mb_file_goes_through_a_url_that_names_nothing_and_works_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);
        var file = new byte[25 * 1024 * 1024];
        Pdf.CopyTo(file, 0);

        var url = await UrlAsync(client, item);

        Assert.StartsWith("https://test.localhost/api/v1/documents/uploads/", url);
        Assert.Matches("^[0-9a-f]{64}$", url.Split('/')[^1]);
        await using (var scope = app.Services.CreateAsyncScope())
            Assert.DoesNotContain(url.Split('/')[^1], await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().UploadTickets.Select(t => t.TokenHash).ToListAsync(TestContext.Current.CancellationToken));

        var uploaded = await PostAsync(app, url, file);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var detail = await DetailAsync(app, (await BodyAsync(uploaded)).GetProperty("id").GetGuid());
        var document = detail.GetProperty("document");
        Assert.Equal((file.LongLength, "Relevé mars", "2024-03-31"), (document.GetProperty("sizeBytes").GetInt64(), document.GetProperty("title").GetString(), document.GetProperty("documentDate").GetString()));
        Assert.Equal(DocumentRoles.Statement, document.GetProperty("links")[0].GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, url, Pdf)).StatusCode);
        Assert.Equal(1, await CountAsync(app));
    }

    [Fact]
    public async Task Of_two_requests_at_once_one_files()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);
        var url = await UrlAsync(client, item);

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(app, url, Pdf)));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
        Assert.Equal(3, answers.Count(a => a.StatusCode == HttpStatusCode.NotFound));
        Assert.Equal(1, await CountAsync(app));
    }

    [Fact]
    public async Task An_expired_url_or_a_made_up_one_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);
        var url = await UrlAsync(client, item);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
            var now = app.Services.GetRequiredService<IClock>().GetCurrentInstant();
            Assert.True((await db.UploadTickets.SingleAsync(TestContext.Current.CancellationToken)).ExpiresAt <= now + RequestDocumentUploadHandler.Lifetime);
            await db.UploadTickets.ExecuteUpdateAsync(t => t.SetProperty(x => x.ExpiresAt, now - NodaTime.Duration.FromSeconds(1)), TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, url, Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, url[..^1] + "x", Pdf)).StatusCode);
        Assert.Equal(0, await CountAsync(app));
    }

    [Fact]
    public async Task A_url_is_refused_on_the_rules_of_upload_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var theirs = await app.NewSubscriptionAsync("Copro de B", app.AdultB, Visibility.Shared);
        await using var client = await ClientAsync(app);
        await using var reader = await ClientAsync(app, TokenPresets.Read);

        Assert.True((await CallAsync(client, "request_document_upload", UploadArguments(theirs))).Error);
        Assert.Contains(DocumentsCodes.InvalidRole, (await CallAsync(client, "request_document_upload", UploadArguments(mine, role: "nope"))).Text);
        Assert.Contains(DocumentsCodes.InvalidField, (await CallAsync(client, "request_document_upload", UploadArguments(mine, date: "2999-01-01"))).Text);
        Assert.Contains("yyyy-mm-dd", (await CallAsync(client, "request_document_upload", UploadArguments(mine, date: "demain"))).Text);
        Assert.Contains("'write'", (await CallAsync(reader, "request_document_upload", UploadArguments(mine))).Text);
    }

    [Fact]
    public async Task A_refused_file_writes_nothing_and_burns_the_url()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:MaxSizeBytes"] = "1024" });
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var notAFile = await UrlAsync(client, item);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PostAsync(app, notAFile, "plain text"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, notAFile, Pdf)).StatusCode);

        var tooLarge = new byte[1025];
        Pdf.CopyTo(tooLarge, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(app, await UrlAsync(client, item), tooLarge)).StatusCode);
        Assert.Equal(0, await CountAsync(app));
    }

    [Fact]
    public async Task A_revoked_token_stops_a_url_already_given()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);
        var url = await UrlAsync(client, item);

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IPersonalAccessTokens>().RevokeAllAsync(app.AdultA, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, url, Pdf)).StatusCode);
        Assert.Equal(0, await CountAsync(app));
    }

    /// <summary>The rights are the ones of the moment the file arrives, not of the moment the URL was given.</summary>
    [Fact]
    public async Task A_target_gone_since_the_url_was_given_refuses_the_file()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);
        var url = await UrlAsync(client, item);

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().UploadTickets
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.TargetId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(app, url, Pdf)).StatusCode);
        Assert.Equal(0, await CountAsync(app));
    }

    [Fact]
    public async Task A_source_reference_already_filed_answers_its_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        await using var client = await ClientAsync(app);

        var first = await BodyAsync(await PostAsync(app, await UrlAsync(client, item, "powimo:1:1"), Pdf));
        var second = await BodyAsync(await PostAsync(app, await UrlAsync(client, item, "powimo:1:1"), Pdf));

        Assert.True(second.GetProperty("existing").GetBoolean());
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(1, await CountAsync(app));
    }
}
