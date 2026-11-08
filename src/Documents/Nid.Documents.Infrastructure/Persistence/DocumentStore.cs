using Microsoft.EntityFrameworkCore;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using NodaTime;
using Npgsql;

namespace Nid.Documents.Infrastructure.Persistence;

public sealed class DocumentStore(DocumentsDbContext db) : IDocumentStore
{
    public async Task CreateAsync(DocumentRecord document, CancellationToken ct)
    {
        var row = new DocumentRow { Id = document.Id };
        Apply(row, document);
        db.Documents.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
                                          { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: DocumentsDbContext.SourceRefIndex })
        {
            db.Entry(row).State = EntityState.Detached;
            throw new DuplicateSourceRefException(e);
        }
    }

    public async Task<DocumentRecord?> FindAsync(Guid id, CancellationToken ct)
        => await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) is { } row ? ToRecord(row) : null;

    public async Task<DocumentRecord?> FindBySourceRefAsync(string sourceRef, CancellationToken ct)
        => await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.SourceRef == sourceRef, ct) is { } row ? ToRecord(row) : null;

    public async Task<IReadOnlyList<DocumentRecord>> FindBySha256Async(string sha256, CancellationToken ct)
        => (await db.Documents.AsNoTracking().Where(d => d.Sha256 == sha256).OrderByDescending(d => d.UploadedAt).ToListAsync(ct)).Select(ToRecord).ToList();

    public async Task<DocumentPageRecord> PageAsync(DocumentPageQuery q, CancellationToken ct)
    {
        List<Guid> readable = [.. q.ReadableItems], persons = [.. q.Persons], dependents = [.. q.Dependents];
        // DocumentRules.CanReadLink: an anchored link is judged on its anchor, whatever its kind.
        var seen = db.Links.Where(l => (l.AnchorItemId != null && readable.Contains(l.AnchorItemId.Value))
            || (l.AnchorItemId == null && (l.TargetKind == TargetKinds.CoreHousehold
                || (l.TargetKind == TargetKinds.CorePerson && persons.Contains(l.TargetId)))));

        var docs = db.Documents.AsNoTracking().Where(d => q.Archived || d.ArchivedAt == null)
            .Where(d => d.UploadedByPersonId == q.Actor || seen.Any(l => l.DocumentId == d.Id));
        if (q.SourceRefPrefix is { } prefix)
            docs = docs.Where(d => d.SourceRef != null && d.SourceRef.StartsWith(prefix));
        if (q.Target is { } target)
            docs = docs.Where(d => seen.Any(l => l.DocumentId == d.Id && l.TargetKind == target.Kind && l.TargetId == target.Id));
        if (q.Anchor is { } anchor)
            docs = docs.Where(d => seen.Any(l => l.DocumentId == d.Id && l.AnchorItemId == anchor));
        if (q.Inbox)
            docs = docs.Where(d => !seen.Any(l => l.DocumentId == d.Id));
        else if (q.SharedItems is { } sharedItems)
        {
            List<Guid> shared = [.. sharedItems];
            docs = docs.Where(d => seen.Any(l => l.DocumentId == d.Id && ((l.AnchorItemId != null && shared.Contains(l.AnchorItemId.Value))
                || (l.AnchorItemId == null && (l.TargetKind == TargetKinds.CoreHousehold
                    || (l.TargetKind == TargetKinds.CorePerson && dependents.Contains(l.TargetId)))))));
        }

        // The facets, before the filters: an option stays offered while it is on.
        var listedLinks = seen.Where(l => docs.Any(d => d.Id == l.DocumentId));
        var roles = await listedLinks.Select(l => l.Role).Distinct().OrderBy(r => r).ToListAsync(ct);
        var links = await listedLinks.Select(l => new { l.TargetKind, l.TargetId, l.AnchorItemId }).Distinct().ToListAsync(ct);
        var zoneId = q.Zone.Id;
        var years = await docs.Select(d => d.DocumentDate != null ? d.DocumentDate.Value.Year : d.UploadedAt.InZone(DateTimeZoneProviders.Tzdb[zoneId]).Date.Year)
            .Distinct().OrderByDescending(y => y).ToListAsync(ct);

        if (q.Role is { } role)
            docs = docs.Where(d => seen.Any(l => l.DocumentId == d.Id && l.Role == role));
        if (q.Linked is { } linked)
            docs = docs.Where(d => seen.Any(l => l.DocumentId == d.Id && l.TargetId == linked));
        if (q.Year is { } year)
        {
            // The shown date: the document's own, else its upload day in the household's zone.
            LocalDate from = new(year, 1, 1), to = new(year + 1, 1, 1);
            Instant start = q.Zone.AtStartOfDay(from).ToInstant(), end = q.Zone.AtStartOfDay(to).ToInstant();
            docs = docs.Where(d => (d.DocumentDate != null && d.DocumentDate >= from && d.DocumentDate < to)
                || (d.DocumentDate == null && d.UploadedAt >= start && d.UploadedAt < end));
        }
        if (q.Search is { } search)
        {
            // Typed text, not a pattern: % and _ match themselves.
            var pattern = $"%{search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            docs = docs.Where(d => EF.Functions.ILike(d.Title, pattern, @"\"));
        }

        var total = await docs.CountAsync(ct);
        // A null date sorts last, as it always did.
        var ordered = docs.OrderBy(d => d.DocumentDate == null).ThenByDescending(d => d.DocumentDate).ThenByDescending(d => d.UploadedAt).ThenBy(d => d.Id);
        var rows = await (q.Take is { } take ? ordered.Skip(q.Skip).Take(take) : ordered).ToListAsync(ct);
        return new DocumentPageRecord(rows.Select(ToRecord).ToList(), total, roles, years,
            links.Select(l => new LinkTargetRecord(l.TargetKind, l.TargetId, l.AnchorItemId)).ToList());
    }

    public async Task<IReadOnlyList<Guid>> AnchorItemsAsync(CancellationToken ct)
        => await db.Links.Where(l => l.AnchorItemId != null).Select(l => l.AnchorItemId!.Value).Distinct().ToListAsync(ct);

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

    public async Task<IReadOnlyList<ExpectedDismissal>> DismissalsAsync(CancellationToken ct)
        => (await db.ExpectedDismissals.AsNoTracking().ToListAsync(ct))
            .Select(d => new ExpectedDismissal(d.TargetKind, d.TargetId, d.Role, d.Period, d.DismissedByPersonId, d.DismissedAt)).ToList();

    public async Task DismissAsync(ExpectedDismissal dismissal, CancellationToken ct)
    {
        var row = new ExpectedDismissalRow
        {
            TargetKind = dismissal.TargetKind, TargetId = dismissal.TargetId, Role = dismissal.Role, Period = dismissal.Period,
            DismissedByPersonId = dismissal.DismissedBy, DismissedAt = dismissal.DismissedAt,
        };
        db.ExpectedDismissals.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Dismissed by a concurrent request first: the first one stays.
            db.Entry(row).State = EntityState.Detached;
        }
    }

    // Tracked row, one property set each: EF writes only what changed, so the other command's field is left alone.
    public async Task AddUploadTicketAsync(UploadTicket ticket, Instant now, CancellationToken ct)
    {
        await db.UploadTickets.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        db.UploadTickets.Add(new UploadTicketRow
        {
            TokenHash = ticket.TokenHash, PersonId = ticket.PersonId, TokenId = ticket.TokenId, FileName = ticket.FileName, Title = ticket.Title,
            DocumentDate = ticket.DocumentDate, Note = ticket.Note, TargetKind = ticket.TargetKind, TargetId = ticket.TargetId, Role = ticket.Role,
            SourceRef = ticket.SourceRef, ExpiresAt = ticket.ExpiresAt,
        });
        await db.SaveChangesAsync(ct);
    }

    // The delete is the claim: of two requests at once, one removes the row and the other removes nothing.
    public async Task<UploadTicket?> ClaimUploadTicketAsync(string tokenHash, Instant now, CancellationToken ct)
    {
        if (await db.UploadTickets.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == tokenHash, ct) is not { } row
            || await db.UploadTickets.Where(t => t.TokenHash == tokenHash).ExecuteDeleteAsync(ct) == 0 || row.ExpiresAt <= now)
            return null;
        return new UploadTicket(row.TokenHash, row.PersonId, row.TokenId, row.FileName, row.Title, row.DocumentDate, row.Note,
            row.TargetKind, row.TargetId, row.Role, row.SourceRef, row.ExpiresAt);
    }

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
        // Issue #414: a deleted target's « pas disponible » go with it.
        db.ExpectedDismissals.RemoveRange(await db.ExpectedDismissals.Where(d => d.TargetKind == target.Kind && d.TargetId == target.Id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    // The target is part of the key: each row is replaced, not updated, so the audit sees a delete and an insert.
    public async Task MoveLinksAsync(DocumentTarget from, DocumentTarget to, CancellationToken ct)
    {
        if (from == to)
            return;
        var rows = await db.Links.Where(l => l.TargetKind == from.Kind && l.TargetId == from.Id).ToListAsync(ct);
        var taken = (await db.Links.Where(l => l.TargetKind == to.Kind && l.TargetId == to.Id).Select(l => l.DocumentId).ToListAsync(ct)).ToHashSet();
        db.Links.RemoveRange(rows);
        foreach (var r in rows.Where(r => !taken.Contains(r.DocumentId)))
            db.Links.Add(new DocumentLinkRow
            {
                DocumentId = r.DocumentId, TargetKind = to.Kind, TargetId = to.Id, AnchorItemId = r.AnchorItemId,
                Role = r.Role, LinkedByPersonId = r.LinkedByPersonId, LinkedAt = r.LinkedAt,
            });
        await db.SaveChangesAsync(ct);
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
        row.SourceRef = d.SourceRef;
    }

    private static DocumentRecord ToRecord(DocumentRow r)
        => new(r.Id, r.Sha256.Trim(), r.SizeBytes, r.MediaType, r.OriginalFilename, r.Title, r.DocumentDate, r.Note,
            r.UploadedByPersonId, r.UploadedAt, r.ArchivedAt, r.SourceRef);
}
