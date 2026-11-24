using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nid.Documents.Application;
using Nid.Documents.Domain;
using NodaTime;

namespace Nid.Documents.Infrastructure.Persistence;

/// <summary>#559: the chunks (EF) and their embeddings (<c>documents.chunk_embedding</c>, in SQL: pgvector's <c>vector</c> has no
/// mapping here). An embedding keeps its model and dimension; each (model, dimension) gets its own partial HNSW index, made
/// each time some are written (<c>if not exists</c>: a lookup in the catalog once it is there).</summary>
public sealed class DocumentIndexStore(DocumentsDbContext db) : IDocumentIndexStore
{
    /// <summary>pgvector's HNSW indexes vectors of up to 2 000 dimensions.</summary>
    public const int MaxIndexedDimension = 2000;

    // ponytail: a document whose text is blanks only (a photo OCR could not read) has a page of "" and is never cut; one of
    // spaces would be picked again each pass (the worker trims every page, so none comes).
    public async Task<IReadOnlyList<Guid>> UnchunkedAsync(int limit, CancellationToken ct)
        => await db.Documents.AsNoTracking()
            .Where(d => db.Texts.Any(t => t.DocumentId == d.Id && t.Text != "") && !db.Chunks.Any(c => c.DocumentId == d.Id))
            .OrderBy(d => d.UploadedAt).Select(d => d.Id).Take(limit).ToListAsync(ct);

    public async Task SaveChunksAsync(Guid document, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        db.Chunks.RemoveRange(await db.Chunks.Where(c => c.DocumentId == document).ToListAsync(ct));
        db.Chunks.AddRange(chunks.Select(c => new DocumentChunkRow
        {
            Id = Guid.CreateVersion7(), DocumentId = document, Ordinal = c.Ordinal, Page = c.Page, Text = c.Text,
        }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid?> UnembeddedAsync(string model, IReadOnlyCollection<Guid> skip, CancellationToken ct)
    {
        var skipped = skip.ToArray();
        var found = await db.Database.SqlQuery<Guid>($"""
            select c.document_id as "Value" from documents.document_chunk c
            where not exists (select 1 from documents.chunk_embedding e where e.chunk_id = c.id and e.model = {model})
              and not (c.document_id = any({skipped}))
            order by c.document_id limit 1
            """).ToListAsync(ct);
        return found is [var id] ? id : null;
    }

    public async Task<IReadOnlyList<ChunkText>> UnembeddedChunksAsync(Guid document, string model, int limit, CancellationToken ct)
        => await db.Database.SqlQuery<ChunkText>($"""
            select c.id as "Id", c.text as "Text" from documents.document_chunk c
            where c.document_id = {document}
              and not exists (select 1 from documents.chunk_embedding e where e.chunk_id = c.id and e.model = {model})
            order by c.ordinal limit {limit}
            """).ToListAsync(ct);

    public async Task SaveEmbeddingsAsync(string model, int dimension, IReadOnlyList<(Guid Chunk, IReadOnlyList<float> Vector)> vectors, Instant at,
        CancellationToken ct)
    {
        var ids = vectors.Select(v => v.Chunk).ToArray();
        var values = vectors.Select(v => "[" + string.Join(',', v.Vector.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]").ToArray();
        await db.Database.ExecuteSqlAsync($"""
            insert into documents.chunk_embedding (chunk_id, model, dimension, embedding, created_at)
            select u.id, {model}, {dimension}, u.v::vector, {at} from unnest({ids}, {values}) as u(id, v)
            on conflict do nothing
            """, ct);
        await IndexAsync(model, dimension, ct);
    }

    public Task DropOtherModelsAsync(Guid document, string model, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            delete from documents.chunk_embedding e using documents.document_chunk c
            where c.id = e.chunk_id and c.document_id = {document} and e.model <> {model}
              and not exists (select 1 from documents.document_chunk c2 where c2.document_id = {document}
                and not exists (select 1 from documents.chunk_embedding e2 where e2.chunk_id = c2.id and e2.model = {model}))
            """, ct);

    /// <summary>The (model, dimension)'s HNSW index, cosine distance, over the vectors cast to their length
    /// (<c>documents.ensure_embedding_index</c>, which quotes the model inside Postgres). A length HNSW does not take gets none
    /// (search then scans).</summary>
    private async Task IndexAsync(string model, int dimension, CancellationToken ct)
    {
        if (dimension is < 1 or > MaxIndexedDimension)
            return;
        await db.Database.ExecuteSqlAsync($"select documents.ensure_embedding_index({model}, {dimension})", ct);
    }
}
