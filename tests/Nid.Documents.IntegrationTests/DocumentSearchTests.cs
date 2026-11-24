using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>#169: the Documents page's search looks in the documents' text, by its words and by its meaning, the most relevant
/// first, with the snippet, its page and whether OCR read it; never in a document the reader cannot read.</summary>
public sealed class DocumentSearchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = Instant.FromUtc(2026, 11, 24, 9, 0);
    private const string Shared = "targetKind=core.household&role=invoice";

    [Fact]
    public async Task A_word_only_inside_a_scanned_document_finds_it_with_its_snippet_and_page()
    {
        await using var app = await StartAsync(new Meanings());
        var scan = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=scan.pdf&title=Scan 0042&{Shared}");
        await TextAsync(app, scan, "ocr", "Page de garde", "Intervention sur la chaudière du sous-sol, pièces et main d'œuvre");
        var other = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=autre.pdf&title=Relevé de compte&{Shared}");
        await TextAsync(app, other, "text-layer", "Solde au 31 octobre");
        await IndexAsync(app);

        var list = await SearchAsync(app, app.AdultA, "chaudiere");

        var found = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(scan, found.GetProperty("id").GetGuid());
        var match = found.GetProperty("match");
        Assert.Equal(2, match.GetProperty("page").GetInt32());
        Assert.True(match.GetProperty("ocr").GetBoolean());
        Assert.False(match.GetProperty("byMeaning").GetBoolean());
        Assert.Contains($"{DocumentMatch.StartMark}chaudière{DocumentMatch.EndMark}", match.GetProperty("snippet").GetString());
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(MeaningSearchStates.Used, list.GetProperty("meaningSearch").GetString());
    }

    [Fact]
    public async Task A_meaning_finds_a_document_its_words_miss_and_an_exact_reference_finds_its_own()
    {
        var meanings = new Meanings();
        await using var app = await StartAsync(meanings);
        var boiler = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=f.pdf&title=Facture 2025-118&{Shared}");
        await TextAsync(app, boiler, "text-layer", "Remplacement du ballon d'eau chaude, référence BX-4471");
        var rent = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=q.pdf&title=Quittance de mars&{Shared}");
        await TextAsync(app, rent, "text-layer", "Quittance de loyer pour le mois de mars");
        await IndexAsync(app);

        var byMeaning = await SearchAsync(app, app.AdultA, "facture chaudière");
        var reference = await SearchAsync(app, app.AdultA, "BX-4471");

        var first = byMeaning.GetProperty("items")[0];
        Assert.Equal(boiler, first.GetProperty("id").GetGuid());
        Assert.True(first.GetProperty("match").GetProperty("byMeaning").GetBoolean());
        Assert.StartsWith("Remplacement du ballon", first.GetProperty("match").GetProperty("snippet").GetString());
        // The rent receipt is not about the boiler: too far by meaning to come back.
        Assert.DoesNotContain(byMeaning.GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetGuid() == rent);
        Assert.Equal(boiler, Assert.Single(reference.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        // The search text went to the gateway as the reader's own, for the household's documents.
        var asked = meanings.Requests.Last(r => r.Texts is ["facture chaudière"]);
        Assert.Equal(DataClasses.Documents, asked.DataClass);
        Assert.Equal([app.AdultA.Value], asked.Concerned!);
    }

    [Fact]
    public async Task A_title_a_word_and_a_meaning_together_rank_first()
    {
        await using var app = await StartAsync(new Meanings());
        var titled = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=c.pdf&title=Chaudière : contrat d'entretien&{Shared}");
        await TextAsync(app, titled, "text-layer", "Visite annuelle de la chaudière gaz");
        var mentioned = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=d.pdf&title=Devis plombier&{Shared}");
        await TextAsync(app, mentioned, "text-layer", "Devis : salle de bains, puis la chaudière plus tard");
        await IndexAsync(app);

        var list = await SearchAsync(app, app.AdultA, "chaudière");

        Assert.Equal([titled, mentioned], list.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList());
    }

    [Fact]
    public async Task Nobody_gets_a_snippet_of_a_document_they_cannot_read()
    {
        await using var app = await StartAsync(new Meanings());
        // Bob's own document, linked to nothing: private to him.
        var secret = await UploadedAsync(app, app.AdultB, Pdf, "fileName=s.pdf&title=Note");
        await TextAsync(app, secret, "text-layer", "Le code de la chaudière est 1234");
        await IndexAsync(app);

        // « Moi »: the scope that holds a document linked to nothing.
        var alice = await SearchAsync(app, app.AdultA, "chaudière", "&scope=me");
        var bob = await SearchAsync(app, app.AdultB, "chaudière", "&scope=me");

        Assert.Equal(0, alice.GetProperty("total").GetInt32());
        Assert.DoesNotContain("1234", alice.GetRawText());
        Assert.Equal(secret, Assert.Single(bob.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Without_an_answer_in_time_the_words_alone_answer_and_say_so()
    {
        await using var app = await StartAsync(new Meanings { Stalls = true });
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=c.pdf&title=Contrat&{Shared}");
        await TextAsync(app, id, "text-layer", "Entretien de la chaudière");
        await using (var scope = Scope(app))
        {
            // The index's own embeddings, before the worker stalls.
            var indexer = new Meanings();
            await new DocumentIndexer(scope.ServiceProvider.GetRequiredService<IDocumentIndexStore>(), scope.ServiceProvider.GetRequiredService<IDocumentTextStore>(),
                scope.ServiceProvider.GetRequiredService<IDocumentStore>(), scope.ServiceProvider.GetRequiredService<DocumentTexts>(), indexer,
                scope.ServiceProvider.GetRequiredService<IClock>()).RunOnceAsync(TestContext.Current.CancellationToken);
        }

        var list = await SearchAsync(app, app.AdultA, "chaudière");

        Assert.Equal(id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(MeaningSearchStates.Unavailable, list.GetProperty("meaningSearch").GetString());
    }

    [Fact]
    public async Task Without_a_model_the_words_alone_answer_and_without_a_search_nothing_is_asked()
    {
        var meanings = new Meanings { Model = null };
        await using var app = await StartAsync(meanings);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=c.pdf&title=Contrat&{Shared}");
        await TextAsync(app, id, "text-layer", "Entretien de la chaudière");
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IDocumentIndexStore>().SaveChunksAsync(id,
                Nid.Documents.Domain.Chunks.Of([(1, "Entretien de la chaudière")]), TestContext.Current.CancellationToken);

        var list = await SearchAsync(app, app.AdultA, "chaudière");
        var all = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/documents?page=1"));

        Assert.Equal(id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(MeaningSearchStates.Off, list.GetProperty("meaningSearch").GetString());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("meaningSearch").ValueKind);
        Assert.Empty(meanings.Requests);
    }

    private Task<NidApp> StartAsync(Meanings meanings)
        => NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<IEmbeddings>(_ => meanings)));

    private static async Task<JsonElement> SearchAsync(NidApp app, PersonId reader, string search, string more = "")
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, $"/api/v1/documents?page=1&q={Uri.EscapeDataString(search)}{more}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task TextAsync(NidApp app, Guid document, string source, params string[] pages)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IDocumentTextStore>().SaveAsync(document,
            [.. pages.Select((text, i) => new DocumentTextPage(i + 1, text, source))], At, TestContext.Current.CancellationToken);
    }

    private static async Task IndexAsync(NidApp app)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<DocumentIndexer>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    /// <summary>
    /// The <c>embed</c> use with two topics: heating (chaudière, ballon, eau chaude) and rent (loyer, quittance), plus a small
    /// constant so no vector is zero. <c>Stalls</c>: the worker never answers, until the caller gives up.
    /// </summary>
    private sealed class Meanings : IEmbeddings
    {
        public string? Model { get; init; } = "fake-embed";

        public bool Stalls { get; init; }

        public List<LlmEmbedRequest> Requests { get; } = [];

        public Task<string?> ModelAsync(CancellationToken ct) => Task.FromResult(Model);

        public async Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            if (Stalls)
                await Task.Delay(Timeout.Infinite, ct);
            if (Model is null)
                return Result.Failure<LlmEmbeddings>(new Failure(JobCodes.NoModel, "No model.", null));
            return Result.Success(new LlmEmbeddings(Model, 3, [.. request.Texts.Select(Vector)]));
        }

        private static IReadOnlyList<float> Vector(string text)
        {
            var t = text.ToLowerInvariant();
            float heating = new[] { "chaudi", "ballon", "eau chaude" }.Any(t.Contains) ? 1 : 0;
            float rent = new[] { "loyer", "quittance" }.Any(t.Contains) ? 1 : 0;
            return [heating, rent, 0.1f];
        }
    }
}
