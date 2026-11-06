using Nid.Documents.Contracts;
using NodaTime;

namespace Nid.Documents.Application;

public sealed record DocumentRecord(
    Guid Id, string Sha256, long SizeBytes, string MediaType, string OriginalFilename, string Title,
    LocalDate? DocumentDate, string? Note, Guid UploadedBy, Instant UploadedAt, Instant? ArchivedAt, string? SourceRef = null);

/// <summary>Syndic documents design §2.1: a concurrent upload stored the same source reference first.</summary>
public sealed class DuplicateSourceRefException(Exception inner) : Exception("The source reference is already taken.", inner);

public sealed record LinkRecord(
    Guid DocumentId, string TargetKind, Guid TargetId, Guid? AnchorItemId, string Role, Guid LinkedBy, Instant LinkedAt);

/// <summary>
/// Issue #420: the list's question, answered in SQL. A link is readable when its anchor is in <paramref name="ReadableItems"/>, or, without
/// an anchor, when it names the household or one of <paramref name="Persons"/>; a document is readable when <paramref name="Actor"/>
/// uploaded it or it holds a readable link. <paramref name="SharedItems"/> (the household scope) keeps the documents with a readable link
/// to one of them, to one of <paramref name="Dependents"/> or to the household. The facets come before <paramref name="Role"/>,
/// <paramref name="Year"/>, <paramref name="Linked"/> and <paramref name="Search"/>; <paramref name="Take"/> null answers every document.
/// </summary>
public sealed record DocumentPageQuery(
    Guid Actor, IReadOnlyCollection<Guid> ReadableItems, IReadOnlyCollection<Guid> Persons, IReadOnlyCollection<Guid> Dependents,
    bool Archived, string? SourceRefPrefix, bool Inbox, DocumentTarget? Target, Guid? Anchor, IReadOnlyCollection<Guid>? SharedItems,
    string? Role, int? Year, Guid? Linked, string? Search, DateTimeZone Zone, int Skip, int? Take);

/// <summary>One page, newest document date first (undated last), the number that matched, and the readable links' roles and targets and the years over the list before the filters.</summary>
public sealed record DocumentPageRecord(
    IReadOnlyList<DocumentRecord> Items, int Total, IReadOnlyList<string> Roles, IReadOnlyList<int> Years, IReadOnlyList<LinkTargetRecord> Links);

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

    /// <summary>Writes the title, date and note only: a concurrent archive is not undone.</summary>
    Task EditAsync(Guid id, string title, LocalDate? documentDate, string? note, CancellationToken ct);

    /// <summary>Writes <c>archived_at</c> only: a concurrent edit is not undone.</summary>
    Task SetArchivedAsync(Guid id, Instant? archivedAt, CancellationToken ct);

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

public sealed class DocumentLinks(IDocumentStore store) : IDocumentLinks
{
    public Task UnlinkTargetAsync(DocumentTarget target, CancellationToken ct) => store.RemoveLinksToAsync(target, ct);

    public Task MoveTargetAsync(DocumentTarget from, DocumentTarget to, CancellationToken ct) => store.MoveLinksAsync(from, to, ct);
}
