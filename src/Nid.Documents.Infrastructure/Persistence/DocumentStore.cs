using Microsoft.EntityFrameworkCore;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using NodaTime;

namespace Nid.Documents.Infrastructure.Persistence;

public sealed class DocumentStore(DocumentsDbContext db) : IDocumentStore
{
    public async Task CreateAsync(DocumentRecord document, CancellationToken ct)
    {
        var row = new DocumentRow { Id = document.Id };
        Apply(row, document);
        db.Documents.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DocumentRecord?> FindAsync(Guid id, CancellationToken ct)
        => await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) is { } row ? ToRecord(row) : null;

    // ponytail: a household holds hundreds of documents, not millions; filtering moves to SQL if listing gets slow.
    public async Task<IReadOnlyList<DocumentRecord>> ListAsync(bool includeArchived, CancellationToken ct)
        => (await db.Documents.AsNoTracking().ToListAsync(ct))
            .Where(d => includeArchived || d.ArchivedAt is null).Select(ToRecord).ToList();

    public async Task<IReadOnlyList<LinkRecord>> LinksOfAsync(IReadOnlyCollection<Guid> documents, CancellationToken ct)
    {
        var wanted = documents.ToList();
        return (await db.Links.AsNoTracking().Where(l => wanted.Contains(l.DocumentId)).ToListAsync(ct))
            .Select(l => new LinkRecord(l.DocumentId, l.TargetKind, l.TargetId, l.AnchorItemId, l.Role, l.LinkedByPersonId, l.LinkedAt)).ToList();
    }

    // Tracked row, one property set each: EF writes only what changed, so the other command's field is left alone.
    public async Task EditAsync(Guid id, string title, LocalDate? documentDate, string? note, CancellationToken ct)
    {
        var row = await db.Documents.SingleAsync(d => d.Id == id, ct);
        (row.Title, row.DocumentDate, row.Note) = (title, documentDate, note);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetArchivedAsync(Guid id, Instant? archivedAt, CancellationToken ct)
    {
        var row = await db.Documents.SingleAsync(d => d.Id == id, ct);
        row.ArchivedAt = archivedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> AddLinkAsync(LinkRecord link, CancellationToken ct)
    {
        if (await db.Links.AnyAsync(l => l.DocumentId == link.DocumentId && l.TargetKind == link.TargetKind && l.TargetId == link.TargetId, ct))
            return false;

        db.Links.Add(new DocumentLinkRow
        {
            DocumentId = link.DocumentId, TargetKind = link.TargetKind, TargetId = link.TargetId, AnchorItemId = link.AnchorItemId,
            Role = link.Role, LinkedByPersonId = link.LinkedBy, LinkedAt = link.LinkedAt,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetRoleAsync(Guid document, DocumentTarget target, string role, CancellationToken ct)
    {
        var row = await FindLinkAsync(document, target, ct);
        if (row is null)
            return false;

        row.Role = role;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveLinkAsync(Guid document, DocumentTarget target, CancellationToken ct)
    {
        var row = await FindLinkAsync(document, target, ct);
        if (row is null)
            return false;

        db.Links.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RemoveLinksToAsync(DocumentTarget target, CancellationToken ct)
    {
        var rows = await db.Links.Where(l => l.TargetKind == target.Kind && l.TargetId == target.Id).ToListAsync(ct);
        db.Links.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private Task<DocumentLinkRow?> FindLinkAsync(Guid document, DocumentTarget target, CancellationToken ct)
        => db.Links.SingleOrDefaultAsync(l => l.DocumentId == document && l.TargetKind == target.Kind && l.TargetId == target.Id, ct);

    private static void Apply(DocumentRow row, DocumentRecord d)
    {
        row.Sha256 = d.Sha256;
        row.SizeBytes = d.SizeBytes;
        row.MediaType = d.MediaType;
        row.OriginalFilename = d.OriginalFilename;
        row.Title = d.Title;
        row.DocumentDate = d.DocumentDate;
        row.Note = d.Note;
        row.UploadedByPersonId = d.UploadedBy;
        row.UploadedAt = d.UploadedAt;
        row.ArchivedAt = d.ArchivedAt;
    }

    private static DocumentRecord ToRecord(DocumentRow r)
        => new(r.Id, r.Sha256.Trim(), r.SizeBytes, r.MediaType, r.OriginalFilename, r.Title, r.DocumentDate, r.Note,
            r.UploadedByPersonId, r.UploadedAt, r.ArchivedAt);
}
