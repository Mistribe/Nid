using Microsoft.EntityFrameworkCore;
using Nid.Documents.Application;
using NodaTime;

namespace Nid.Documents.Infrastructure.Persistence;

public sealed class DocumentTextStore(DocumentsDbContext db, IDocumentStore documents) : IDocumentTextStore
{
    public async Task QueuedAsync(Guid document, Guid job, Instant at, CancellationToken ct)
    {
        if (await db.TextJobs.SingleOrDefaultAsync(j => j.DocumentId == document, ct) is { } row)
            row.JobId = job;
        else
            db.TextJobs.Add(new DocumentTextJobRow { DocumentId = document, JobId = job, QueuedAt = at });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentRecord>> NeverQueuedAsync(int limit, CancellationToken ct)
    {
        var ids = await db.Documents.AsNoTracking()
            .Where(d => !db.TextJobs.Any(j => j.DocumentId == d.Id))
            .OrderBy(d => d.UploadedAt).ThenBy(d => d.Id).Select(d => d.Id).Take(limit).ToListAsync(ct);
        return ids.Count == 0 ? [] : (await documents.FindManyAsync(ids, ct)).OrderBy(d => d.UploadedAt).ThenBy(d => d.Id).ToList();
    }

    public async Task SaveAsync(Guid document, IReadOnlyList<DocumentTextPage> pages, Instant at, CancellationToken ct)
    {
        if (!await db.Documents.AnyAsync(d => d.Id == document, ct))
            return;
        db.Texts.RemoveRange(await db.Texts.Where(t => t.DocumentId == document).ToListAsync(ct));
        db.Chunks.RemoveRange(await db.Chunks.Where(c => c.DocumentId == document).ToListAsync(ct)); // #559: cut again by the indexer
        db.Texts.AddRange(pages.Select(p => new DocumentTextRow { DocumentId = document, Page = p.Number, Text = p.Text, Source = p.Source, ExtractedAt = at }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentTextPage>> PagesAsync(Guid document, CancellationToken ct)
        => await db.Texts.AsNoTracking().Where(t => t.DocumentId == document).OrderBy(t => t.Page)
            .Select(t => new DocumentTextPage(t.Page, t.Text, t.Source)).ToListAsync(ct);
}
