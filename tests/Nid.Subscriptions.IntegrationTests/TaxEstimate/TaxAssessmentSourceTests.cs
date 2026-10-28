using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.TestSupport;
using Nid.Subscriptions.IntegrationTests.Documents;
using static Nid.Subscriptions.IntegrationTests.Documents.DocumentEndpointTests;

namespace Nid.Subscriptions.IntegrationTests.TaxEstimate;

/// <summary>Tax assessments spec §5: the source document of a notice, linked to each declarant.</summary>
public class TaxAssessmentSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/tax/assessments";

    private static object Declarant(PersonId person, int rank) => new { personId = person.Value, rank };

    private static Task<HttpResponseMessage> PostAsync(NidApp app, PersonId actor, int year, Guid? documentId, params PersonId[] declarants)
        => app.RawAsync(actor, HttpMethod.Post, Route,
            new { year, declarants = declarants.Select((p, i) => Declarant(p, i + 1)).ToArray(), documentId });

    private static async Task<JsonElement> LinksAsync(NidApp app, PersonId actor, Guid document)
        => (await JsonAsync(await GetAsync(app, actor, document))).GetProperty("document").GetProperty("links");

    private static async Task<JsonElement> AssessmentAsync(NidApp app, PersonId actor, Guid id)
        => await JsonAsync(await app.RawAsync(actor, HttpMethod.Get, $"{Route}/{id}"));

    [Fact]
    public async Task A_joint_notice_links_its_pdf_to_both_declarants()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);

        var created = await PostAsync(app, app.AdultA, 2023, document, app.AdultA, app.AdultB);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        foreach (var reader in new[] { app.AdultA, app.AdultB })
        {
            var link = Assert.Single((await LinksAsync(app, reader, document)).EnumerateArray(), l => l.GetProperty("targetKind").GetString() == TargetKinds.CorePerson);
            Assert.Equal((reader.Value, DocumentRoles.TaxNotice), (link.GetProperty("targetId").GetGuid(), link.GetProperty("role").GetString()));
        }
    }

    [Fact]
    public async Task An_unreadable_document_is_refused_before_anything_is_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await UploadedAsync(app, app.AdultB, Pdf);

        var response = await PostAsync(app, app.AdultA, 2023, secret, app.AdultA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}?scope=me"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_document_the_author_reads_but_cannot_edit_is_kept_as_source_without_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultB, Visibility.Shared);
        var document = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");

        var created = await PostAsync(app, app.AdultA, 2023, document, app.AdultA);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(document, (await JsonAsync(created)).GetProperty("documentId").GetGuid());
        Assert.Single((await LinksAsync(app, app.AdultB, document)).EnumerateArray());
    }

    [Fact]
    public async Task A_correction_without_document_keeps_the_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        var view = await JsonAsync(await PostAsync(app, app.AdultA, 2023, document, app.AdultA));

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{view.GetProperty("id").GetGuid()}",
            new { year = 2023, declarants = new[] { Declarant(app.AdultA, 1) }, version = view.GetProperty("version").GetUInt32() });

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(document, (await JsonAsync(corrected)).GetProperty("documentId").GetGuid());
    }

    [Fact]
    public async Task A_correction_to_a_joint_notice_links_the_stored_source_to_the_new_declarant()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);
        var view = await JsonAsync(await PostAsync(app, app.AdultA, 2019, document, app.AdultA));
        var id = view.GetProperty("id").GetGuid();

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id}",
            new { year = 2019, declarants = new[] { Declarant(app.AdultA, 1), Declarant(app.AdultB, 2) }, version = view.GetProperty("version").GetUInt32() });

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(document, (await AssessmentAsync(app, app.AdultB, id)).GetProperty("documentId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, document)).StatusCode);
    }

    [Fact]
    public async Task A_correction_giving_a_new_document_replaces_the_source_and_links_both_declarants()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await UploadedAsync(app, app.AdultA, Pdf);
        var second = await UploadedAsync(app, app.AdultA, Pdf);
        var view = await JsonAsync(await PostAsync(app, app.AdultA, 2019, first, app.AdultA));
        var id = view.GetProperty("id").GetGuid();

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id}",
            new { year = 2019, declarants = new[] { Declarant(app.AdultA, 1), Declarant(app.AdultB, 2) }, version = view.GetProperty("version").GetUInt32(), documentId = second });

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(second, (await AssessmentAsync(app, app.AdultB, id)).GetProperty("documentId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, second)).StatusCode);
    }

    [Fact]
    public async Task A_source_its_author_cannot_edit_gets_no_link_for_the_new_declarant()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultB, Visibility.Shared);
        var document = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        var view = await JsonAsync(await PostAsync(app, app.AdultA, 2019, document, app.AdultA));
        var id = view.GetProperty("id").GetGuid();

        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Route}/{id}",
            new { year = 2019, declarants = new[] { Declarant(app.AdultA, 1), Declarant(app.AdultB, 2) }, version = view.GetProperty("version").GetUInt32() });

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        // A reads the source but cannot edit it: no tax_notice link is created (B reads it through the shared item it is filed on).
        Assert.DoesNotContain((await LinksAsync(app, app.AdultB, document)).EnumerateArray(), l => l.GetProperty("targetKind").GetString() == TargetKinds.CorePerson);
        Assert.Equal(document, (await AssessmentAsync(app, app.AdultA, id)).GetProperty("documentId").GetGuid());
    }

    [Fact]
    public async Task Linking_through_the_other_adults_person_is_refused_outside_tax_notices()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{DocumentEndpointTests.Documents}/{document}/links",
            new { targetKind = TargetKinds.CorePerson, targetId = app.AdultB.Value, role = DocumentRoles.TaxNotice });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
