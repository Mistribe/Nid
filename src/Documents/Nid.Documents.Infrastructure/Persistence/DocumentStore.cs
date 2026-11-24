using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using NodaTime;
using Npgsql;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

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
        // (kind, id) as one key, as Postgres prints a uuid: a pair list does not translate.
        List<string> own = [.. (q.OwnUnanchored ?? []).Select(t => $"{t.Kind}:{t.Id}")];
        // DocumentRules.CanReadLink: an anchored link is judged on its anchor, whatever its kind; another unanchored kind is its uploader's (#84).
        var seen = db.Links.Where(l => (l.AnchorItemId != null && readable.Contains(l.AnchorItemId.Value))
            || (l.AnchorItemId == null && (l.TargetKind == TargetKinds.CoreHousehold
                || (l.TargetKind == TargetKinds.CorePerson && persons.Contains(l.TargetId))
                || (own.Contains(l.TargetKind + ":" + l.TargetId.ToString())
                    && db.Documents.Any(d => d.Id == l.DocumentId && d.UploadedByPersonId == q.Actor)))));

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
        var facets = links.Select(l => new LinkTargetRecord(l.TargetKind, l.TargetId, l.AnchorItemId)).ToList();
        if (q.Search is { } search)
            return await SearchAsync(docs, search, q, roles, years, facets, ct);

        var total = await docs.CountAsync(ct);
        var rows = await (q.Take is { } take ? ByDate(docs).Skip(q.Skip).Take(take) : ByDate(docs)).ToListAsync(ct);
        return new DocumentPageRecord(rows.Select(ToRecord).ToList(), total, roles, years, facets);
    }

    // A null date sorts last, as it always did.
    private static IOrderedQueryable<DocumentRow> ByDate(IQueryable<DocumentRow> docs)
        => docs.OrderBy(d => d.DocumentDate == null).ThenByDescending(d => d.DocumentDate).ThenByDescending(d => d.UploadedAt).ThenBy(d => d.Id);

    /// <summary>The nearest chunks a search by meaning compares.</summary>
    private const int NearestChunks = 50;

    /// <summary>Reciprocal rank fusion's constant: a document's score is the sum, over the lists that found it, of 1 / (60 + rank).</summary>
    private const int FusionK = 60;

    /// <summary>How much farther (cosine distance, 0 to 2) than the nearest chunk a chunk may be and still count as found by meaning.</summary>
    private const double MeaningMargin = 0.2;

    /// <summary>Farther than this, a chunk is not about the search at all, nearest or not (a reference number has no meaning to share).</summary>
    private const double MeaningMaxDistance = 0.6;

    /// <summary>The start of a chunk found by meaning only: its first characters.</summary>
    private const int MeaningSnippet = 240;

    /// <summary>
    /// #169: among the documents the filters left (so only those the reader reads), three ranked lists: the titles that hold the text,
    /// the chunks whose words match it (French, without accents, #582), and the chunks nearest its meaning (#559). They are fused by
    /// reciprocal rank, each document by its best chunk; equal scores keep the date order. The page's documents found in their text
    /// get their snippet.
    /// </summary>
    private async Task<DocumentPageRecord> SearchAsync(IQueryable<DocumentRow> docs, string search, DocumentPageQuery q,
        IReadOnlyList<string> roles, IReadOnlyList<int> years, IReadOnlyList<LinkTargetRecord> facets, CancellationToken ct)
    {
        // Typed text, not a pattern: % and _ match themselves.
        var pattern = $"%{search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
        var titled = await ByDate(docs.Where(d => EF.Functions.ILike(d.Title, pattern, @"\"))).Select(d => d.Id).ToListAsync(ct);
        var ids = await docs.Select(d => d.Id).ToListAsync(ct);
        var words = await db.Database.SqlQuery<ChunkHit>($"""
            select distinct on (c.document_id) c.document_id as "DocumentId", c.id as "ChunkId", ts_rank_cd(c.search, w)::float8 as "Score"
            from documents.document_chunk c, websearch_to_tsquery('documents.nid_french', {search}) w
            where c.document_id = any({ids}) and c.search @@ w
            order by c.document_id, ts_rank_cd(c.search, w) desc, c.ordinal
            """).ToListAsync(ct);
        List<ChunkHit> near = [];
        if (q.Meaning is { } meaning)
        {
            var vector = "[" + string.Join(',', meaning.Vector.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";
            // ponytail: an exact scan of the model's vectors, without the HNSW index (#559), which needs the length as a literal;
            // fine for a household's thousands of chunks, the index's turn when that grows.
            near = await db.Database.SqlQuery<ChunkHit>($"""
                select c.document_id as "DocumentId", c.id as "ChunkId", (e.embedding <=> {vector}::vector)::float8 as "Score"
                from documents.chunk_embedding e join documents.document_chunk c on c.id = e.chunk_id
                where e.model = {meaning.Model} and e.dimension = {meaning.Dimension} and c.document_id = any({ids})
                order by e.embedding <=> {vector}::vector, c.id
                limit {NearestChunks}
                """).ToListAsync(ct);
        }

        var byWords = words.OrderByDescending(h => h.Score).ThenBy(h => h.DocumentId).ToList();
        // A document's best chunk by meaning: the nearest of its chunks among the nearest overall. The nearest are always some, related
        // or not: only those about as near as the best one count, or every document would come back for any search.
        // ponytail: one margin for every model; a per-model calibration if a model's distances spread differently.
        var byMeaning = near.GroupBy(h => h.DocumentId).Select(g => g.MinBy(h => h.Score)!).OrderBy(h => h.Score).ToList();
        if (byMeaning is [var nearest, ..])
            byMeaning = byMeaning.Where(h => h.Score <= Math.Min(nearest.Score + MeaningMargin, MeaningMaxDistance)).ToList();
        var scores = new Dictionary<Guid, double>();
        foreach (var ranked in new[] { titled, byWords.Select(h => h.DocumentId).ToList(), byMeaning.Select(h => h.DocumentId).ToList() })
            for (var rank = 0; rank < ranked.Count; rank++)
                scores[ranked[rank]] = scores.GetValueOrDefault(ranked[rank]) + 1.0 / (FusionK + rank + 1);

        var found = scores.Keys.ToList();
        var dated = await ByDate(docs.Where(d => found.Contains(d.Id))).Select(d => d.Id).ToListAsync(ct);
        var fused = dated.OrderByDescending(id => scores[id]).ToList(); // a stable sort: equal scores keep the date order
        var pageIds = (q.Take is { } take ? fused.Skip(q.Skip).Take(take) : fused).ToList();
        var rows = (await docs.Where(d => pageIds.Contains(d.Id)).ToListAsync(ct)).ToDictionary(d => d.Id);

        var wordChunk = byWords.Where(h => pageIds.Contains(h.DocumentId)).ToDictionary(h => h.DocumentId, h => h.ChunkId);
        var meaningChunk = byMeaning.Where(h => pageIds.Contains(h.DocumentId) && !wordChunk.ContainsKey(h.DocumentId))
            .ToDictionary(h => h.DocumentId, h => h.ChunkId);
        return new DocumentPageRecord(pageIds.Select(id => ToRecord(rows[id])).ToList(), fused.Count, roles, years, facets,
            await MatchesAsync(search, [.. wordChunk.Values], [.. meaningChunk.Values], ct));
    }

    /// <summary>
    /// #169: each chunk's snippet: the words found, marked (<c>ts_headline</c>, as plain text), else the start of the chunk found by
    /// meaning; its page, and whether that page was read by OCR.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, DocumentMatch>> MatchesAsync(string search, List<Guid> wordChunks, List<Guid> meaningChunks,
        CancellationToken ct)
    {
        if (wordChunks.Count + meaningChunks.Count == 0)
            return new Dictionary<Guid, DocumentMatch>();
        List<Guid> all = [.. wordChunks, .. meaningChunks];
        var options = $"StartSel={DocumentMatch.StartMark}, StopSel={DocumentMatch.EndMark}, MaxWords=30, MinWords=12, MaxFragments=2, FragmentDelimiter=\" … \"";
        var snippets = await db.Database.SqlQuery<ChunkSnippet>($"""
            select c.document_id as "DocumentId", c.page as "Page",
                case when c.id = any({wordChunks})
                    then ts_headline('documents.nid_french', c.text, websearch_to_tsquery('documents.nid_french', {search}), {options})
                    else left(c.text, {MeaningSnippet}) end as "Snippet",
                exists (select 1 from documents.document_text t where t.document_id = c.document_id and t.page = c.page and t.source = 'ocr') as "Ocr",
                not (c.id = any({wordChunks})) as "ByMeaning"
            from documents.document_chunk c
            where c.id = any({all})
            """).ToListAsync(ct);
        return snippets.ToDictionary(s => s.DocumentId, s => new DocumentMatch(s.Snippet.Trim(), s.Page, s.Ocr, s.ByMeaning));
    }

    private sealed record ChunkHit(Guid DocumentId, Guid ChunkId, double Score);

    private sealed record ChunkSnippet(Guid DocumentId, int Page, string Snippet, bool Ocr, bool ByMeaning);

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
            .Select(ToLink).ToList();
    }

    public async Task<IReadOnlyList<LinkRecord>> LinksToAsync(string kind, IReadOnlyCollection<Guid> targets, CancellationToken ct)
    {
        var wanted = targets.ToList();
        return (await db.Links.AsNoTracking().Where(l => l.TargetKind == kind && wanted.Contains(l.TargetId)).ToListAsync(ct)).Select(ToLink).ToList();
    }

    public async Task<IReadOnlyList<DocumentTarget>> UnanchoredTargetsOfAsync(Guid uploader, CancellationToken ct)
        => (await db.Links.AsNoTracking().Where(l => l.AnchorItemId == null && l.TargetKind != TargetKinds.CorePerson && l.TargetKind != TargetKinds.CoreHousehold
                && db.Documents.Any(d => d.Id == l.DocumentId && d.UploadedByPersonId == uploader))
            .Select(l => new { l.TargetKind, l.TargetId }).Distinct().ToListAsync(ct))
            .Select(l => new DocumentTarget(l.TargetKind, l.TargetId)).ToList();

    public async Task<IReadOnlyList<DocumentRecord>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToList();
        return (await db.Documents.AsNoTracking().Where(d => wanted.Contains(d.Id)).ToListAsync(ct)).Select(ToRecord).ToList();
    }

    public async Task ReanchorLinksAsync(string kind, IReadOnlyCollection<Guid> targets, Guid? anchor, CancellationToken ct)
    {
        var wanted = targets.ToList();
        // Tracked, not a bulk update: the write audit keeps each link's new anchor.
        foreach (var row in await db.Links.Where(l => l.TargetKind == kind && wanted.Contains(l.TargetId) && l.AnchorItemId != anchor).ToListAsync(ct))
            row.AnchorItemId = anchor;
        await db.SaveChangesAsync(ct);
    }

    private static LinkRecord ToLink(DocumentLinkRow l) => new(l.DocumentId, l.TargetKind, l.TargetId, l.AnchorItemId, l.Role, l.LinkedByPersonId, l.LinkedAt);

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

    public async Task<Result> EditAsync(Guid id, string title, LocalDate? documentDate, string? note, uint version, CancellationToken ct)
    {
        var row = await db.Documents.SingleAsync(d => d.Id == id, ct);
        (row.Title, row.DocumentDate, row.Note) = (title, documentDate, note);
        return await SaveAtAsync(row, version, ct);
    }

    public async Task<Result> SetArchivedAsync(Guid id, Instant? archivedAt, uint version, CancellationToken ct)
    {
        var row = await db.Documents.SingleAsync(d => d.Id == id, ct);
        row.ArchivedAt = archivedAt;
        return await SaveAtAsync(row, version, ct);
    }

    /// <summary>#506: the update names the version the caller read, so a change made in between answers a conflict instead of being lost.</summary>
    private async Task<Result> SaveAtAsync(DocumentRow row, uint version, CancellationToken ct)
    {
        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure(DocumentConflict.Failure());
        }
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
        db.UploadTickets.RemoveRange(await db.UploadTickets.Where(t => t.TargetKind == target.Kind && t.TargetId == target.Id).ToListAsync(ct));
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
            r.UploadedByPersonId, r.UploadedAt, r.ArchivedAt, r.SourceRef, r.Version);
}
