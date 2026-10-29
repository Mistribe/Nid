using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Tax assessments spec §4: who a notice's PDF would reach through the declarants' person links, before saving.</summary>
public class TaxNoticePreviewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Task<HttpResponseMessage> PreviewAsync(NidApp app, PersonId actor, Guid document, params PersonId[] declarants)
        => app.RawAsync(actor, HttpMethod.Get,
            $"{DocumentApi.Documents}/{document}/tax-notice-preview?{string.Join('&', declarants.Select(d => $"declarants={d.Value}"))}");

    private static async Task<Guid[]> GainedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("gainedReaders").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToArray();
    }

    [Fact]
    public async Task Previews_the_other_declarant_as_a_new_reader()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal([app.AdultB.Value], await GainedAsync(await PreviewAsync(app, app.AdultA, document, app.AdultA, app.AdultB)));
    }

    [Fact]
    public async Task An_already_readable_document_gains_nobody()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var document = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");

        Assert.Empty(await GainedAsync(await PreviewAsync(app, app.AdultA, document, app.AdultA, app.AdultB)));
    }

    [Fact]
    public async Task Refuses_when_the_actor_is_not_a_declarant()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(app, app.AdultA, document, app.AdultB)).StatusCode);
    }

    [Fact]
    public async Task Refuses_an_unreadable_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await UploadedAsync(app, app.AdultB, Pdf);

        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(app, app.AdultA, secret, app.AdultA, app.AdultB)).StatusCode);
    }

    [Fact]
    public async Task Writes_no_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);

        await GainedAsync(await PreviewAsync(app, app.AdultA, document, app.AdultA, app.AdultB));

        var links = (await JsonAsync(await GetAsync(app, app.AdultA, document))).GetProperty("document").GetProperty("links");
        Assert.DoesNotContain(links.EnumerateArray(), l => l.GetProperty("targetKind").GetString() == TargetKinds.CorePerson);
    }

    [Fact]
    public async Task Refuses_a_document_the_actor_can_read_but_not_edit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultB, Visibility.Shared);
        var document = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");

        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(app, app.AdultA, document, app.AdultA, app.AdultB)).StatusCode);
    }

    [Fact]
    public async Task Refuses_a_declarant_who_is_not_an_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(app, app.AdultA, document, app.AdultA, new PersonId(Guid.NewGuid()))).StatusCode);
    }
}
