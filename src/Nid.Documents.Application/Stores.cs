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

public interface IDocumentStore
{
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

    Task<int> RemoveLinksToAsync(DocumentTarget target, CancellationToken ct);

    /// <summary>Re-points the links to <paramref name="from"/> at <paramref name="to"/>; a document already linked to <paramref name="to"/> loses its <paramref name="from"/> link.</summary>
    Task MoveLinksAsync(DocumentTarget from, DocumentTarget to, CancellationToken ct);

    /// <summary>The ids of the documents holding a link to the target, whoever may read it.</summary>
    Task<IReadOnlySet<Guid>> DocumentsLinkedToAsync(DocumentTarget target, CancellationToken ct);

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
