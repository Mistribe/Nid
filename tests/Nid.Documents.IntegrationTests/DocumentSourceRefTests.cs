using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>Syndic documents design §2.1: a source reference makes an upload idempotent and never reveals a document.</summary>
public class DocumentSourceRefTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static UploadDocumentCommand Upload(ItemId item, string? sourceRef)
        => new(Pdf, "appel.pdf", "Appel de fonds", null, null, new DocumentTarget(TargetKinds.CoreItem, item.Value), DocumentRoles.CoproCall, sourceRef);

    private static async Task<UploadedDocument> UploadedAsync(NidApp app, PersonId actor, UploadDocumentCommand command)
    {
        var (response, failure) = await app.InvokeAsync(actor, command);
        Assert.Null(failure);
        return Assert.IsType<UploadedDocument>(response);
    }

    private static async Task<IReadOnlyList<DocumentRecord>> RowsAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(includeArchived: true, CancellationToken.None);
    }

    [Fact]
    public async Task Same_source_ref_returns_the_existing_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);

        var first = await UploadedAsync(app, app.AdultA, Upload(home, "powimo:7028:1"));
        var second = await UploadedAsync(app, app.AdultA, Upload(home, "powimo:7028:1"));

        Assert.False(first.Existing);
        Assert.Equal(new UploadedDocument(first.Id, true), second);
        Assert.Equal("powimo:7028:1", Assert.Single(await RowsAsync(app)).SourceRef);
    }

    [Fact]
    public async Task Source_ref_unreadable_by_the_actor_is_refused_without_an_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewSubscriptionAsync("Privé", app.AdultB, Visibility.Private);
        var mine = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var theirs = await UploadedAsync(app, app.AdultB, Upload(hidden, "powimo:7028:1"));

        var (response, failure) = await app.InvokeAsync(app.AdultA, Upload(mine, "powimo:7028:1"));

        Assert.Null(response);
        Assert.Equal(DocumentsCodes.SourceRefTaken, failure!.Code);
        Assert.DoesNotContain(theirs.Id.ToString(), failure.Message);
        Assert.Single(await RowsAsync(app));
    }

    [Fact]
    public async Task Concurrent_uploads_of_one_source_ref_make_one_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);

        var both = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => UploadedAsync(app, app.AdultA, Upload(home, "powimo:7028:1"))));

        Assert.Single(both.Select(u => u.Id).Distinct());
        Assert.Single(both, u => u.Existing);
        Assert.Single(await RowsAsync(app));
    }

    [Fact]
    public async Task Source_ref_is_trimmed_and_blank_means_none()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);

        var trimmed = await UploadedAsync(app, app.AdultA, Upload(home, "  powimo:1 "));
        await UploadedAsync(app, app.AdultA, Upload(home, "  "));
        await UploadedAsync(app, app.AdultA, Upload(home, "  "));

        var rows = await RowsAsync(app);
        Assert.Equal("powimo:1", rows.Single(r => r.Id == trimmed.Id).SourceRef);
        Assert.Equal(2, rows.Count(r => r.SourceRef is null));
    }

    [Fact]
    public async Task Source_ref_longer_than_200_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);

        var (_, failure) = await app.InvokeAsync(app.AdultA, Upload(home, new string('x', 201)));

        Assert.Equal(DocumentsCodes.InvalidField, failure!.Code);
        Assert.Equal("sourceRef", failure.Metadata!["field"]);
    }

    [Fact]
    public async Task Unlinked_document_keeps_its_source_ref()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Copro", app.AdultA, Visibility.Shared);
        var first = await UploadedAsync(app, app.AdultA, Upload(home, "powimo:7028:1"));
        var (_, unlinked) = await app.InvokeAsync(app.AdultA, new UnlinkDocumentCommand(first.Id, new DocumentTarget(TargetKinds.CoreItem, home.Value)));
        Assert.Null(unlinked);

        Assert.Equal(new UploadedDocument(first.Id, true), await UploadedAsync(app, app.AdultA, Upload(home, "powimo:7028:1")));
    }
}
