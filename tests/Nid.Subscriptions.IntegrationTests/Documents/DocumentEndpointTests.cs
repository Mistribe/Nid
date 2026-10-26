using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Documents;

/// <summary>Documents design §5, §7: upload, read, download, edit and archive through the API.</summary>
public class DocumentEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    internal const string Documents = "/api/v1/documents";
    internal static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");

    /// <summary>A multipart upload; <paramref name="query"/> keeps the old spelling: <c>fileName</c> names the file, the others are form fields.</summary>
    internal static Task<HttpResponseMessage> UploadAsync(NidApp app, PersonId actor, byte[] body, string query = "fileName=bail.pdf")
        => app.RawContentAsync(actor, HttpMethod.Post, Documents, Multipart(body, query));

    internal static MultipartFormDataContent Multipart(byte[] body, string query)
    {
        var fields = System.Web.HttpUtility.ParseQueryString(query);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(body);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (fields["fileName"] is { } name)
        {
            // filename*, so a name HttpClient would refuse to quote (a quote, a bidi mark) still travels.
            file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=file; filename*=UTF-8''{Uri.EscapeDataString(name)}");
            form.Add(file);
        }
        foreach (var key in fields.AllKeys.Where(k => k != "fileName"))
            form.Add(new StringContent(fields[key]!), key!);
        return form;
    }

    internal static async Task<Guid> UploadedAsync(NidApp app, PersonId actor, byte[] body, string query = "fileName=bail.pdf")
    {
        var response = await UploadAsync(app, actor, body, query);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    internal static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    internal static Task<HttpResponseMessage> GetAsync(NidApp app, PersonId actor, Guid id, string suffix = "")
        => app.RawAsync(actor, HttpMethod.Get, $"{Documents}/{id}{suffix}");

    private static string[] StoredFiles(NidApp app)
        => Directory.Exists(app.DocumentsRoot) ? Directory.GetFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories) : [];

    private static string Pad(int size) => "%PDF-" + new string('x', size - 5);

    [Fact]
    public async Task Uploads_a_document_its_uploader_alone_reads()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await UploadedAsync(app, app.AdultA, Pdf, "fileName=Bail%20sign%C3%A9.pdf");

        var detail = await JsonAsync(await GetAsync(app, app.AdultA, id));
        var document = detail.GetProperty("document");
        Assert.Equal(("Bail signé", "application/pdf", 0, "Bail signé.pdf"),
            (document.GetProperty("title").GetString(), document.GetProperty("mediaType").GetString(),
                document.GetProperty("links").GetArrayLength(), detail.GetProperty("originalFilename").GetString()));
        Assert.True(document.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, id)).StatusCode);
    }

    [Fact]
    public async Task Serves_the_bytes_as_an_attachment_under_the_cleaned_name()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf, "fileName=" + Uri.EscapeDataString("../x/evil\"‮name;1.pdf"));

        var response = await GetAsync(app, app.AdultA, id, "/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("evilname;1.pdf", disposition.FileNameStar);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store, private", string.Join(", ", response.Headers.CacheControl!.ToString().Split(", ").Order()));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, id, "/content")).StatusCode);
    }

    [Fact]
    public async Task Two_uploads_of_the_same_bytes_make_two_documents_and_one_file()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var a = await UploadedAsync(app, app.AdultA, Pdf);
        var b = await UploadedAsync(app, app.AdultB, Pdf);

        Assert.NotEqual(a, b);
        Assert.Single(StoredFiles(app));
    }

    [Fact]
    public async Task An_edit_after_an_archive_keeps_the_archive()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Documents}/{id}/archive");

        var edit = await app.RawContentAsync(app.AdultA, HttpMethod.Put, $"{Documents}/{id}",
            new StringContent("""{"title":"Nouveau"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var document = (await JsonAsync(await GetAsync(app, app.AdultA, id))).GetProperty("document");
        Assert.Equal(("Nouveau", true), (document.GetProperty("title").GetString(), document.GetProperty("archived").GetBoolean()));
    }

    [Fact]
    public async Task Refuses_an_executable_named_as_a_pdf()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await UploadAsync(app, app.AdultA, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00], "fileName=x.pdf");

        await AssertRefusedAsync(response, HttpStatusCode.UnsupportedMediaType, DocumentsCodes.UnsupportedType);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task Accepts_a_body_at_the_limit_and_refuses_one_byte_more()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:MaxSizeBytes"] = "1024" });

        await AssertRefusedAsync(await UploadAsync(app, app.AdultA, Encoding.ASCII.GetBytes(Pad(1025))),
            HttpStatusCode.RequestEntityTooLarge, DocumentsCodes.TooLarge);
        Assert.Empty(StoredFiles(app));

        await UploadedAsync(app, app.AdultA, Encoding.ASCII.GetBytes(Pad(1024)));
        Assert.Single(StoredFiles(app));
    }

    [Fact]
    public async Task Refuses_a_declared_multipart_body_far_over_the_limit()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:MaxSizeBytes"] = "1024" });

        await AssertRefusedAsync(await UploadAsync(app, app.AdultA, Encoding.ASCII.GetBytes(Pad(200_000))),
            HttpStatusCode.RequestEntityTooLarge, DocumentsCodes.TooLarge);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task Refuses_a_chunked_multipart_body_over_the_limit_and_stores_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Documents:MaxSizeBytes"] = "1024" });
        using var form = Multipart(Encoding.ASCII.GetBytes(Pad(1025)), "fileName=a.pdf");
        var chunked = new ChunkedContent(await form.ReadAsByteArrayAsync());
        chunked.Headers.ContentType = form.Headers.ContentType;

        await AssertRefusedAsync(await app.RawContentAsync(app.AdultA, HttpMethod.Post, Documents, chunked),
            HttpStatusCode.RequestEntityTooLarge, DocumentsCodes.TooLarge);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task Uploads_with_a_title_a_date_and_a_note()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await UploadedAsync(app, app.AdultA, Pdf, "fileName=bail.pdf&title=Mon%20bail&date=2025-03-01&note=Signe");

        var detail = await JsonAsync(await GetAsync(app, app.AdultA, id));
        var document = detail.GetProperty("document");
        Assert.Equal(("Mon bail", "2025-03-01", "Signe"),
            (document.GetProperty("title").GetString(), document.GetProperty("documentDate").GetString(), detail.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task Refuses_a_form_without_a_file_part()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await UploadAsync(app, app.AdultA, Pdf, "title=Sans%20fichier");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task Refuses_a_raw_octet_stream_upload()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var raw = new ByteArrayContent(Pdf);
        raw.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await app.RawContentAsync(app.AdultA, HttpMethod.Post, Documents, raw);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task A_link_to_an_item_opens_the_document_to_its_readers()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var mine = await app.NewSubscriptionAsync("Banque", app.AdultA, Visibility.Private);

        var open = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=acte.pdf&targetKind=core.item&targetId={shared.Value}&role=deed");
        var closed = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=releve.pdf&targetKind=core.item&targetId={mine.Value}&role=statement");

        var links = (await JsonAsync(await GetAsync(app, app.AdultB, open))).GetProperty("document").GetProperty("links");
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal((TargetKinds.CoreItem, shared.Value, "deed", "Box"),
            (link.GetProperty("targetKind").GetString(), link.GetProperty("targetId").GetGuid(), link.GetProperty("role").GetString(),
                link.GetProperty("label").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, closed)).StatusCode);
    }

    [Fact]
    public async Task A_link_says_whether_the_reader_may_write_its_target()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");

        async Task<bool> CanWriteAsync(PersonId actor)
            => Assert.Single((await JsonAsync(await GetAsync(app, actor, id))).GetProperty("document").GetProperty("links").EnumerateArray())
                .GetProperty("canWrite").GetBoolean();

        Assert.True(await CanWriteAsync(app.AdultA));
        Assert.False(await CanWriteAsync(app.AdultB)); // reads A's shared item, does not write it
    }

    [Fact]
    public async Task Refuses_a_link_to_the_other_adults_private_item_and_stores_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await app.NewSubscriptionAsync("Secret", app.AdultB, Visibility.Private);

        var response = await UploadAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={theirs.Value}&role=other");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task Refuses_a_bad_link_before_storing_anything()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await AssertRefusedAsync(await UploadAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.person&targetId={app.AdultA.Value}&role=nope"),
            HttpStatusCode.BadRequest, DocumentsCodes.InvalidRole);
        await AssertRefusedAsync(await UploadAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=bank.account&targetId={Guid.NewGuid()}&role=other"),
            HttpStatusCode.BadRequest, DocumentsCodes.UnknownTargetKind);
        await AssertRefusedAsync(await UploadAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.person&targetId={Guid.NewGuid()}&role=other"),
            HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
        // Another adult's person is not the uploader's to link to, and is not said to exist.
        await AssertRefusedAsync(await UploadAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.person&targetId={app.AdultB.Value}&role=payslip"),
            HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
        Assert.Empty(StoredFiles(app));

        var household = await UploadedAsync(app, app.AdultA, Pdf, "fileName=a.pdf&targetKind=core.household&role=tax_notice");
        var link = Assert.Single((await JsonAsync(await GetAsync(app, app.AdultB, household))).GetProperty("document").GetProperty("links").EnumerateArray());
        Assert.Equal("Foyer", link.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Edits_only_with_the_right_to_edit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        var edit = new { title = "Facture box", documentDate = "2025-10-01", note = "Octobre" };

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Put, $"{Documents}/{id}", edit);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False((await JsonAsync(await GetAsync(app, app.AdultB, id))).GetProperty("document").GetProperty("canEdit").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Documents}/{id}", edit)).StatusCode);
        var detail = await JsonAsync(await GetAsync(app, app.AdultA, id));
        Assert.Equal(("Facture box", "2025-10-01", "Octobre"),
            (detail.GetProperty("document").GetProperty("title").GetString(), detail.GetProperty("document").GetProperty("documentDate").GetString(),
                detail.GetProperty("note").GetString()));

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Documents}/{id}", new { title = " " }),
            HttpStatusCode.BadRequest, DocumentsCodes.InvalidField);
        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Documents}/{id}", new { title = "x", documentDate = "2099-01-01" }),
            HttpStatusCode.BadRequest, DocumentsCodes.InvalidField);
    }

    [Fact]
    public async Task Archives_and_unarchives()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Documents}/{id}/archive")).StatusCode);
        Assert.True((await JsonAsync(await GetAsync(app, app.AdultA, id))).GetProperty("document").GetProperty("archived").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Documents}/{id}/archive")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Documents}/{id}/unarchive")).StatusCode);
        Assert.False((await JsonAsync(await GetAsync(app, app.AdultA, id))).GetProperty("document").GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task A_stored_file_gone_answers_content_missing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        File.Delete(Assert.Single(StoredFiles(app)));

        await AssertRefusedAsync(await GetAsync(app, app.AdultA, id, "/content"), HttpStatusCode.NotFound, DocumentsCodes.ContentMissing);
    }

    [Fact]
    public async Task Listing_by_target_needs_both_halves_and_a_known_kind()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Documents}?targetKind=core.item")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Documents}?targetId={Guid.NewGuid()}")).StatusCode);
        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Documents}?targetKind=bank.account&targetId={Guid.NewGuid()}"),
            HttpStatusCode.BadRequest, DocumentsCodes.UnknownTargetKind);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Documents}?targetKind=core.household")).StatusCode);
    }

    /// <summary>A body with no <c>Content-Length</c>: the server only learns its size by reading it.</summary>
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
