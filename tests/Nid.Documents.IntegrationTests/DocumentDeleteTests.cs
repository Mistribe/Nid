using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>#600: a document deleted by whoever edits it, with its links, its file, its text job, audited.</summary>
public sealed class DocumentDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Its_editor_deletes_it_with_its_links_its_file_and_its_text_job()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf, "fileName=bail.pdf&targetKind=core.household&role=other");
        Assert.Equal(1, await CountAsync(app, $"select count(*) as \"Value\" from ai.job where file_ref = 'documents:{id}'"));

        await DeletedAsync(app, app.AdultB, id); // the household's link: Bob edits it too

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultA, id)).StatusCode);
        Assert.Equal(0, await CountAsync(app, $"select count(*) as \"Value\" from documents.document_link where document_id = '{id}'"));
        Assert.Equal(0, await CountAsync(app, $"select count(*) as \"Value\" from ai.job where file_ref = 'documents:{id}'"));
        Assert.Empty(Directory.EnumerateFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories));
        // The row and its link, each in the write log, by Bob.
        Assert.Equal(2, await CountAsync(app, $"select count(*) as \"Value\" from platform.audit_log where action = 'delete' and entity_key like '%{id}%' and person_id = '{app.AdultB.Value}'"));
    }

    [Fact]
    public async Task Someone_who_does_not_read_it_meets_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultA, Pdf); // « À classer »: Alice's alone

        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(app, app.AdultB, id, await VersionAsync(app, app.AdultA, id))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultA, id)).StatusCode);
        Assert.Single(Directory.EnumerateFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_two_documents_share_stays_until_the_last_one_goes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await UploadedAsync(app, app.AdultA, Pdf, "fileName=a.pdf");
        var second = await UploadedAsync(app, app.AdultA, Pdf, "fileName=b.pdf");

        await DeletedAsync(app, app.AdultA, first);

        Assert.Single(Directory.EnumerateFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories));
        var content = await GetAsync(app, app.AdultA, second, "/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(Pdf, await content.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        await DeletedAsync(app, app.AdultA, second);
        Assert.Empty(Directory.EnumerateFiles(app.DocumentsRoot, "*", SearchOption.AllDirectories));
    }

    private static async Task<int> CountAsync(NidApp app, string sql)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
#pragma warning disable EF1002 // the tests' own fixed SQL, with ids they made
        return (int)(await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().Database.SqlQueryRaw<long>(sql)
            .ToListAsync(TestContext.Current.CancellationToken)).Single();
#pragma warning restore EF1002
    }
}
