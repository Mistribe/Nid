using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>#559: a document's text in chunks, each with its page, a French full-text vector and an embedding of the household's
/// current model, indexed in the background; payslips stay on the premises.</summary>
public sealed class DocumentIndexTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = Instant.FromUtc(2026, 11, 23, 9, 0);

    [Fact]
    public async Task A_document_with_text_gets_chunks_with_their_page_a_french_search_vector_and_an_embedding()
    {
        var model = new FakeEmbeddings();
        await using var app = await StartAsync(model);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await TextAsync(app, id, "Quittance de loyer de janvier", "Montant réglé : 900 euros");

        await IndexAsync(app);

        Assert.Equal([1, 2], await ScalarsAsync<int>(app, $"select page as \"Value\" from documents.document_chunk where document_id = '{id}' order by ordinal"));
        // #582, French without accents: « quittances loyers » finds « Quittance de loyer », « regle » « réglé ».
        Assert.Equal(1, await CountAsync(app, $"select count(*) as \"Value\" from documents.document_chunk where document_id = '{id}' and search @@ plainto_tsquery('{DocumentsDbContext.FrenchSearch}', 'quittances loyers')"));
        Assert.Equal(1, await CountAsync(app, $"select count(*) as \"Value\" from documents.document_chunk where document_id = '{id}' and search @@ plainto_tsquery('{DocumentsDbContext.FrenchSearch}', 'regle')"));
        Assert.Equal(2, await CountAsync(app, $"select count(*) as \"Value\" from documents.chunk_embedding e join documents.document_chunk c on c.id = e.chunk_id where c.document_id = '{id}' and e.model = 'fake-embed' and e.dimension = 3"));
        Assert.Equal(1, await CountAsync(app, "select count(*) as \"Value\" from pg_indexes where schemaname = 'documents' and tablename = 'chunk_embedding' and indexdef like '%hnsw%'"));
        var asked = Assert.Single(model.Requests);
        Assert.Equal(DataClasses.Documents, asked.DataClass);
        Assert.Equal([app.AdultA.Value], asked.Concerned!);
        Assert.Empty(asked.Kinds!); // #556: unfiled, no type a cloud provider may read
    }

    [Fact]
    public async Task Changing_the_model_embeds_again_then_drops_the_old_vectors()
    {
        var model = new FakeEmbeddings();
        await using var app = await StartAsync(model);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await TextAsync(app, id, "Avis d'échéance");
        await IndexAsync(app);

        // Any model name, quote included: Postgres quotes it in the index's predicate (documents.ensure_embedding_index).
        model.Model = "o'other embed";
        await IndexAsync(app);

        Assert.Equal(["o'other embed"], await ScalarsAsync<string>(app, $"select distinct e.model as \"Value\" from documents.chunk_embedding e join documents.document_chunk c on c.id = e.chunk_id where c.document_id = '{id}'"));
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(1, await CountAsync(app, "select count(*) as \"Value\" from pg_indexes where schemaname = 'documents' and tablename = 'chunk_embedding' and indexdef like '%hnsw%' and indexdef like '%o''''other embed%'"));
    }

    [Fact]
    public async Task A_payslip_never_leaves_the_premises_and_both_adults_of_a_household_link_are_concerned()
    {
        var model = new FakeEmbeddings();
        await using var app = await StartAsync(model);
        var id = await UploadedAsync(app, app.AdultA, Pdf, "fileName=bulletin.pdf&targetKind=core.household&role=payslip");
        await TextAsync(app, id, "Bulletin de paie de janvier");

        await IndexAsync(app);

        var asked = Assert.Single(model.Requests);
        Assert.Equal(DataClasses.LocalOnly, asked.DataClass);
        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), asked.Concerned!.Order());
    }

    [Fact]
    public async Task A_document_refused_on_its_own_does_not_hold_back_the_others()
    {
        var model = new FakeEmbeddings { RefuseWhen = text => text.Contains("privé", StringComparison.Ordinal) };
        await using var app = await StartAsync(model);
        var refused = await UploadedAsync(app, app.AdultA, Pdf, "fileName=a.pdf");
        var other = await UploadedAsync(app, app.AdultB, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% b\n%%EOF\n"), "fileName=b.pdf");
        await TextAsync(app, refused, "Un courrier privé");
        await TextAsync(app, other, "Une facture d'eau");

        await IndexAsync(app);

        Assert.Equal(0, await EmbeddedAsync(app, refused));
        Assert.Equal(1, await EmbeddedAsync(app, other));
    }

    [Fact]
    public async Task With_no_model_nothing_is_embedded_and_the_chunks_wait()
    {
        var model = new FakeEmbeddings { Model = null };
        await using var app = await StartAsync(model);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await TextAsync(app, id, "Contrat de bail");

        await IndexAsync(app);

        Assert.Equal(1, await CountAsync(app, $"select count(*) as \"Value\" from documents.document_chunk where document_id = '{id}'"));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task A_new_text_replaces_the_chunks_and_deleting_the_document_deletes_them_with_their_vectors()
    {
        var model = new FakeEmbeddings();
        await using var app = await StartAsync(model);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await TextAsync(app, id, "Première lecture");
        await IndexAsync(app);
        await TextAsync(app, id, "Seconde lecture");
        await IndexAsync(app);

        Assert.Equal(["Seconde lecture"], await ScalarsAsync<string>(app, $"select text as \"Value\" from documents.document_chunk where document_id = '{id}'"));
        Assert.Equal(1, await EmbeddedAsync(app, id));

        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().Documents.Where(d => d.Id == id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await CountAsync(app, $"select count(*) as \"Value\" from documents.document_chunk where document_id = '{id}'"));
        Assert.Equal(0, await CountAsync(app, "select count(*) as \"Value\" from documents.chunk_embedding"));
    }

    private Task<NidApp> StartAsync(FakeEmbeddings model)
        => NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<IEmbeddings>(_ => model)));

    private static async Task TextAsync(NidApp app, Guid document, params string[] pages)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IDocumentTextStore>().SaveAsync(document,
            [.. pages.Select((text, i) => new DocumentTextPage(i + 1, text, "text-layer"))], At, TestContext.Current.CancellationToken);
    }

    private static async Task IndexAsync(NidApp app)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<DocumentIndexer>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static Task<int> EmbeddedAsync(NidApp app, Guid document)
        => CountAsync(app, $"select count(*) as \"Value\" from documents.chunk_embedding e join documents.document_chunk c on c.id = e.chunk_id where c.document_id = '{document}'");

    private static async Task<int> CountAsync(NidApp app, string sql) => (int)(await ScalarsAsync<long>(app, sql)).Single();

    private static async Task<List<T>> ScalarsAsync<T>(NidApp app, string sql)
    {
        await using var scope = Scope(app);
#pragma warning disable EF1002 // the tests' own fixed SQL, with ids they made
        return await scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().Database.SqlQueryRaw<T>(sql).ToListAsync(TestContext.Current.CancellationToken);
#pragma warning restore EF1002
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    /// <summary>The <c>embed</c> use as the indexer sees it: a model, and three numbers per text (its length, then 1 and 0).</summary>
    private sealed class FakeEmbeddings : IEmbeddings
    {
        public string? Model { get; set; } = "fake-embed";

        public Func<string, bool> RefuseWhen { get; init; } = _ => false;

        public List<LlmEmbedRequest> Requests { get; } = [];

        public Task<string?> ModelAsync(CancellationToken ct) => Task.FromResult(Model);

        public Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            if (request.Texts.Any(RefuseWhen))
                return Task.FromResult(Result.Failure<LlmEmbeddings>(new Failure(JobCodes.OffsiteNotAllowed, "No.", null)));
            return Task.FromResult(Result.Success(new LlmEmbeddings(Model!, 3,
                [.. request.Texts.Select(t => (IReadOnlyList<float>)[t.Length, 1f, 0f])])));
        }
    }
}
