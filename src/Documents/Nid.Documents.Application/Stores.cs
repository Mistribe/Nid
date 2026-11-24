using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Documents.Application;

public sealed record DocumentRecord(
    Guid Id, string Sha256, long SizeBytes, string MediaType, string OriginalFilename, string Title,
    LocalDate? DocumentDate, string? Note, Guid UploadedBy, Instant UploadedAt, Instant? ArchivedAt, string? SourceRef = null, uint Version = 0);

/// <summary>#506: the answer to a request naming a version the document no longer has (409).</summary>
public static class DocumentConflict
{
    public static ConflictFailure Failure() => new("The document changed since it was read.") { Code = DocumentsCodes.Conflict };
}

/// <summary>Syndic documents design §2.1: a concurrent upload stored the same source reference first.</summary>
public sealed class DuplicateSourceRefException(Exception inner) : Exception("The source reference is already taken.", inner);

public sealed record LinkRecord(
    Guid DocumentId, string TargetKind, Guid TargetId, Guid? AnchorItemId, string Role, Guid LinkedBy, Instant LinkedAt);

/// <summary>
/// Issue #420: the list's question, answered in SQL. A link is readable when its anchor is in <paramref name="ReadableItems"/>, or, without
/// an anchor, when it names the household or one of <paramref name="Persons"/>, or one of <paramref name="OwnUnanchored"/> on a document
/// <paramref name="Actor"/> uploaded (#84: the targets they write, from <c>DocumentRules</c>); a document is readable when <paramref name="Actor"/>
/// uploaded it or it holds a readable link. <paramref name="SharedItems"/> (the household scope) keeps the documents with a readable link
/// to one of them, to one of <paramref name="Dependents"/> or to the household. The facets come before <paramref name="Role"/>,
/// <paramref name="Year"/>, <paramref name="Linked"/> and <paramref name="Search"/>; <paramref name="Take"/> null answers every document.
/// </summary>
public sealed record DocumentPageQuery(
    Guid Actor, IReadOnlyCollection<Guid> ReadableItems, IReadOnlyCollection<Guid> Persons, IReadOnlyCollection<Guid> Dependents,
    bool Archived, string? SourceRefPrefix, bool Inbox, DocumentTarget? Target, Guid? Anchor, IReadOnlyCollection<Guid>? SharedItems,
    string? Role, int? Year, Guid? Linked, string? Search, DateTimeZone Zone, int Skip, int? Take, IReadOnlyCollection<DocumentTarget>? OwnUnanchored = null,
    SearchMeaning? Meaning = null);

/// <summary>#169: the search text's vector, of the household's <c>embed</c> model; the chunks of that model are compared with it.</summary>
public sealed record SearchMeaning(string Model, int Dimension, IReadOnlyList<float> Vector);

/// <summary>One page, newest document date first (undated last), the number that matched, and the readable links' roles and targets and the years over the list before the filters.
/// #169: with a search, the most relevant first, and <c>Matches</c> the best place in the text of each document found there.</summary>
public sealed record DocumentPageRecord(
    IReadOnlyList<DocumentRecord> Items, int Total, IReadOnlyList<string> Roles, IReadOnlyList<int> Years, IReadOnlyList<LinkTargetRecord> Links,
    IReadOnlyDictionary<Guid, DocumentMatch>? Matches = null);

public sealed record LinkTargetRecord(string TargetKind, Guid TargetId, Guid? AnchorItemId);

public interface IDocumentStore
{
    Task<DocumentPageRecord> PageAsync(DocumentPageQuery query, CancellationToken ct);

    /// <summary>Every item a link counts through: the items whose rights decide who reads what.</summary>
    Task<IReadOnlyList<Guid>> AnchorItemsAsync(CancellationToken ct);

    Task CreateAsync(DocumentRecord document, CancellationToken ct);

    Task<DocumentRecord?> FindAsync(Guid id, CancellationToken ct);

    Task<DocumentRecord?> FindBySourceRefAsync(string sourceRef, CancellationToken ct);

    /// <summary>Every document stored with these bytes, archived or not, the latest upload first.</summary>
    Task<IReadOnlyList<DocumentRecord>> FindBySha256Async(string sha256, CancellationToken ct);

    Task<IReadOnlyList<DocumentRecord>> ListAsync(bool includeArchived, CancellationToken ct);

    Task<IReadOnlyList<LinkRecord>> LinksOfAsync(IReadOnlyCollection<Guid> documents, CancellationToken ct);

    /// <summary>#84: the links to these targets of one kind.</summary>
    Task<IReadOnlyList<LinkRecord>> LinksToAsync(string kind, IReadOnlyCollection<Guid> targets, CancellationToken ct);

    Task<IReadOnlyList<DocumentRecord>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>#84: the targets with no anchor, a person and the household aside, of the documents <paramref name="uploader"/> uploaded.</summary>
    Task<IReadOnlyList<DocumentTarget>> UnanchoredTargetsOfAsync(Guid uploader, CancellationToken ct);

    /// <summary>#84: sets the anchor of the links to these targets.</summary>
    Task ReanchorLinksAsync(string kind, IReadOnlyCollection<Guid> targets, Guid? anchor, CancellationToken ct);

    /// <summary>Writes the title, date and note, if the row is still at <paramref name="version"/> (#506); else <c>documents.conflict</c>.</summary>
    Task<Result> EditAsync(Guid id, string title, LocalDate? documentDate, string? note, uint version, CancellationToken ct);

    /// <summary>Writes <c>archived_at</c>, if the row is still at <paramref name="version"/> (#506); else <c>documents.conflict</c>.</summary>
    Task<Result> SetArchivedAsync(Guid id, Instant? archivedAt, uint version, CancellationToken ct);

    /// <summary>#600: deletes the row, if it is still at <paramref name="version"/>, else <c>documents.conflict</c>. Its links go one by
    /// one, so the audit sees each; its text, its job's row, its chunks and their embeddings by cascade.</summary>
    Task<Result> DeleteAsync(Guid id, uint version, CancellationToken ct);

    /// <summary>#600: the lock on a file's bytes, held until the open transaction ends: taken by an upload around storing them and
    /// committing its row, and by a delete around checking no row uses them and removing them.</summary>
    Task LockFileAsync(string sha256, CancellationToken ct);

    /// <summary>False when the document is already linked to the target.</summary>
    Task<bool> AddLinkAsync(LinkRecord link, CancellationToken ct);

    Task<bool> SetRoleAsync(Guid document, DocumentTarget target, string role, CancellationToken ct);

    Task<bool> RemoveLinkAsync(Guid document, DocumentTarget target, CancellationToken ct);

    /// <summary>The links to the target and its « pas disponible » (#414); answers the number of links removed.</summary>
    Task<int> RemoveLinksToAsync(DocumentTarget target, CancellationToken ct);

    /// <summary>Re-points the links to <paramref name="from"/> at <paramref name="to"/>; a document already linked to <paramref name="to"/> loses its <paramref name="from"/> link.</summary>
    Task MoveLinksAsync(DocumentTarget from, DocumentTarget to, CancellationToken ct);

    /// <summary>Issue #414: every « pas disponible » of the household; a few per item, read whole.</summary>
    Task<IReadOnlyList<ExpectedDismissal>> DismissalsAsync(CancellationToken ct);

    /// <summary>Issue #414: stores the dismissal; one already stored for that target, role and period is kept as it is.</summary>
    Task DismissAsync(ExpectedDismissal dismissal, CancellationToken ct);

    /// <summary>#305: stores the ticket and clears those expired before <paramref name="now"/>.</summary>
    Task AddUploadTicketAsync(UploadTicket ticket, Instant now, CancellationToken ct);

    /// <summary>#305: takes the ticket out, once, whoever asks at the same time; null when it is unknown, used or expired.</summary>
    Task<UploadTicket?> ClaimUploadTicketAsync(string tokenHash, Instant now, CancellationToken ct);
}

public sealed class DocumentLinks(IDocumentStore store, DocumentAccess access) : IDocumentLinks
{
    public Task UnlinkTargetAsync(DocumentTarget target, CancellationToken ct) => store.RemoveLinksToAsync(target, ct);

    public Task MoveTargetAsync(DocumentTarget from, DocumentTarget to, CancellationToken ct) => store.MoveLinksAsync(from, to, ct);

    public Task ReanchorTargetsAsync(string kind, IReadOnlyCollection<Guid> targetIds, Guid? anchor, CancellationToken ct)
        => store.ReanchorLinksAsync(kind, targetIds, anchor, ct);

    public async Task<IReadOnlySet<Guid>> TargetIdsWithLinksAsync(string kind, IReadOnlyCollection<Guid> targetIds, CancellationToken ct)
        => (await store.LinksToAsync(kind, targetIds, ct)).Select(l => l.TargetId).ToHashSet();

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<LinkedDocumentView>>> ReadableOfTargetsAsync(
        string kind, IReadOnlyCollection<Guid> targetIds, PersonId reader, CancellationToken ct)
    {
        var linked = await store.LinksToAsync(kind, targetIds, ct);
        if (linked.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<LinkedDocumentView>>();
        // The link itself must be readable: another readable link of the document does not show it on this target. Archived documents are left out, as the documents list does by default. One rights check, whatever the number of targets.
        var documents = (await store.FindManyAsync(linked.Select(l => l.DocumentId).Distinct().ToList(), ct)).Where(d => d.ArchivedAt is null).ToDictionary(d => d.Id);
        var facts = await access.FactsAsync(reader, linked.Select(l => l.AnchorItemId).OfType<Guid>().ToList(), ct,
            DocumentAccess.Unanchored(linked.Where(l => documents.TryGetValue(l.DocumentId, out var d) && d.UploadedBy == reader.Value)));
        return linked.Where(l => documents.TryGetValue(l.DocumentId, out var d) && DocumentRules.CanReadLink(DocumentAccess.Facts(l, d.UploadedBy), facts))
            .GroupBy(l => l.TargetId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LinkedDocumentView>)[.. g.Select(l => new LinkedDocumentView(l.DocumentId, documents[l.DocumentId].Title))
                .OrderBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase)]);
    }
}
