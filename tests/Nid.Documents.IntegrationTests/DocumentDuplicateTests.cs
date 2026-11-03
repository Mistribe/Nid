using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>#297: an upload that asks for it is warned of a file already in a document the actor reads, and of no other.</summary>
public class DocumentDuplicateTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static UploadDocumentCommand Upload(ItemId? item = null, bool warn = true)
        => new(Pdf, "bail.pdf", null, null, null, item is { } i ? new DocumentTarget(TargetKinds.CoreItem, i.Value) : null,
            item is null ? null : DocumentRoles.Lease, WarnDuplicate: warn);

    private static async Task<UploadedDocument> InvokedAsync(NidApp app, PersonId actor, UploadDocumentCommand command)
    {
        var (response, failure) = await app.InvokeAsync(actor, command);
        Assert.Null(failure);
        return Assert.IsType<UploadedDocument>(response);
    }

    private static async Task<int> RowsAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(includeArchived: true, CancellationToken.None)).Count;
    }

    [Fact]
    public async Task Same_file_answers_with_the_document_holding_it_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await app.NewSubscriptionAsync("Assurance", app.AdultA, Visibility.Shared);
        var first = await InvokedAsync(app, app.AdultA, Upload(home));

        var second = await InvokedAsync(app, app.AdultA, Upload());

        Assert.Equal(first.Id, second.Id);
        Assert.True(second.Existing);
        Assert.Equal("bail", second.Duplicate!.Title);
        Assert.Equal("Assurance", Assert.Single(second.Duplicate.Links).Label);
        Assert.Equal(1, await RowsAsync(app));
    }

    [Fact]
    public async Task Without_the_warning_the_same_file_makes_a_second_document()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await InvokedAsync(app, app.AdultA, Upload());

        var second = await InvokedAsync(app, app.AdultA, Upload(warn: false));

        Assert.Null(second.Duplicate);
        Assert.Equal(2, await RowsAsync(app));
    }

    [Fact]
    public async Task A_copy_the_actor_does_not_read_is_not_revealed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await InvokedAsync(app, app.AdultB, Upload());

        var mine = await InvokedAsync(app, app.AdultA, Upload());

        Assert.False(mine.Existing);
        Assert.Null(mine.Duplicate);
        Assert.Equal(2, await RowsAsync(app));
    }

    [Fact]
    public async Task An_archived_copy_counts_and_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await InvokedAsync(app, app.AdultA, Upload());
        var (_, archived) = await app.InvokeAsync(app.AdultA, new SetDocumentArchivedCommand(first.Id, true), TestContext.Current.CancellationToken);
        Assert.Null(archived);

        var second = await InvokedAsync(app, app.AdultA, Upload());

        Assert.Equal(first.Id, second.Id);
        Assert.True(second.Duplicate!.Archived);
    }

    [Fact]
    public async Task Route_answers_409_with_the_document_only_when_asked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await DocumentApi.UploadedAsync(app, app.AdultA, Pdf);

        var warned = await UploadAsync(app, app.AdultA, Pdf, "fileName=bail.pdf&warnDuplicate=true");
        await AssertRefusedAsync(warned, HttpStatusCode.Conflict, DocumentsCodes.Duplicate);
        var duplicate = (await JsonAsync(warned)).GetProperty("duplicate");
        Assert.Equal(first, duplicate.GetProperty("id").GetGuid());
        Assert.Equal("bail", duplicate.GetProperty("title").GetString());
        Assert.False(duplicate.GetProperty("archived").GetBoolean());

        Assert.NotEqual(first, await DocumentApi.UploadedAsync(app, app.AdultA, Pdf));
    }
}
