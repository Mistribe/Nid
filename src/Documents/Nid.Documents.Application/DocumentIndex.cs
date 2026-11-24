using Nid.Ai.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using NodaTime;

namespace Nid.Documents.Application;

/// <summary>A chunk waiting for its embedding.</summary>
public sealed record ChunkText(Guid Id, string Text);

/// <summary>#559: a document's chunks (<c>documents.document_chunk</c>) and their embeddings (<c>documents.chunk_embedding</c>).</summary>
public interface IDocumentIndexStore
{
    /// <summary>Documents with text and no chunks yet (a new text drops the old chunks), oldest first.</summary>
    Task<IReadOnlyList<Guid>> UnchunkedAsync(int limit, CancellationToken ct);

    /// <summary>Replaces the document's chunks, and their embeddings with them.</summary>
    Task SaveChunksAsync(Guid document, IReadOnlyList<Chunk> chunks, CancellationToken ct);

    /// <summary>A document with a chunk that has no embedding of <paramref name="model"/>, none of <paramref name="skip"/>.</summary>
    Task<Guid?> UnembeddedAsync(string model, IReadOnlyCollection<Guid> skip, CancellationToken ct);

    Task<IReadOnlyList<ChunkText>> UnembeddedChunksAsync(Guid document, string model, int limit, CancellationToken ct);

    Task SaveEmbeddingsAsync(string model, int dimension, IReadOnlyList<(Guid Chunk, IReadOnlyList<float> Vector)> vectors, Instant at, CancellationToken ct);

    /// <summary>Once every chunk of the document has an embedding of <paramref name="model"/>, those of other models go.</summary>
    Task DropOtherModelsAsync(Guid document, string model, CancellationToken ct);
}

/// <summary>
/// #559, in the background: cuts each new text in chunks (<see cref="Chunks"/>), then embeds the chunks that have no vector of
/// the household's current <c>embed</c> model through the gateway, a document at a time and in batches. A payslip is
/// <c>local_only</c>; the adults concerned are the text's (<see cref="DocumentTexts"/>). Changing the model embeds everything
/// again, and a document's old vectors go once it has the new ones, so search always has one or the other.
/// </summary>
public sealed class DocumentIndexer(
    IDocumentIndexStore index, IDocumentTextStore texts, IDocumentStore documents, DocumentTexts adults, IEmbeddings embeddings, IClock clock)
{
    public const int ChunkBatch = 32;
    private const int TextsPerPass = 50;
    private const int DocumentsPerPass = 20;

    /// <summary>A refusal about this document alone: the others go on. Any other (no model, no worker) ends the pass.</summary>
    private static readonly HashSet<string> OnItsOwn =
        [JobCodes.OffsiteNotAllowed, JobCodes.NoSafeWorker, JobCodes.CloudNotAllowed, JobCodes.InvalidInput, JobCodes.ResultInvalid];

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var id in await index.UnchunkedAsync(TextsPerPass, ct))
            await index.SaveChunksAsync(id, Chunks.Of((await texts.PagesAsync(id, ct)).Select(p => (p.Number, p.Text))), ct);

        if (await embeddings.ModelAsync(ct) is not { } model)
            return;
        var skip = new HashSet<Guid>();
        for (var n = 0; n < DocumentsPerPass && await index.UnembeddedAsync(model, skip, ct) is { } id; n++)
        {
            switch (await EmbedAsync(id, model, ct))
            {
                case Step.Done:
                    await index.DropOtherModelsAsync(id, model, ct);
                    break;
                case Step.Skip:
                    skip.Add(id);
                    break;
                default:
                    return;
            }
        }
    }

    private async Task<Step> EmbedAsync(Guid id, string model, CancellationToken ct)
    {
        if (await documents.FindAsync(id, ct) is not { } document)
            return Step.Skip;
        var links = await documents.LinksOfAsync([id], ct);
        var dataClass = DocumentTexts.DataClassOf(links);
        var concerned = await adults.ConcernedAsync(document.UploadedBy, links, ct);
        // #556: its types, for a cloud provider's embeddings (allowed_kinds).
        var kinds = links.Select(l => l.Role).Distinct().ToList();
        while (await index.UnembeddedChunksAsync(id, model, ChunkBatch, ct) is { Count: > 0 } chunks)
        {
            var embedded = await embeddings.EmbedAsync(new LlmEmbedRequest([.. chunks.Select(c => c.Text)], dataClass, concerned, Kinds: kinds), ct);
            if (!embedded.TryGetValue(out var result))
                return embedded.TryGetFailure(out var failure) && OnItsOwn.Contains(failure!.Code) ? Step.Skip : Step.Stop;
            if (result.Model != model)
                return Step.Stop; // the household changed the model meanwhile: the next pass starts over with it
            await index.SaveEmbeddingsAsync(model, result.Dimension, [.. chunks.Select(c => c.Id).Zip(result.Vectors)], clock.GetCurrentInstant(), ct);
        }

        return Step.Done;
    }

    private enum Step
    {
        Done,
        Skip,
        Stop,
    }
}
