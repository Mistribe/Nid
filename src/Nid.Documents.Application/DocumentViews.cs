using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using NodaTime;

namespace Nid.Documents.Application;

/// <summary><c>AnchorItemId</c>: the item the link counts through (the item itself for a <c>core.item</c>), null for a person or the household.</summary>
public sealed record DocumentLinkView(string TargetKind, Guid TargetId, Guid? AnchorItemId, string Role, string Label, bool CanWrite);

/// <summary>
/// Design §7: <c>Links</c> holds only the links whose target the reader reads; the others are absent, not counted.
/// <c>UploadedBy</c> is the document's owner, the person it is private to while it has no link (the list's owner column).
/// </summary>
public sealed record DocumentSummary(
    Guid Id, string Title, string MediaType, long SizeBytes, LocalDate? DocumentDate, Instant UploadedAt, Guid UploadedBy, bool Archived, bool CanEdit,
    IReadOnlyList<DocumentLinkView> Links, string? SourceRef)
{
    /// <summary>The label of a link whose target the resolvers no longer return; the link still counts through its anchor.</summary>
    public const string DeletedTargetLabel = "Élément supprimé";

    /// <summary><paramref name="labels"/>: from <see cref="DocumentAccess.LabelsAsync"/>.</summary>
    public static DocumentSummary Of(ReadableDocument read, IReadOnlyDictionary<(string Kind, Guid Id), string> labels)
    {
        var d = read.Document;
        var links = read.Links.Where(l => DocumentRules.CanReadLink(DocumentAccess.Facts(l), read.Actor))
            .OrderBy(l => l.LinkedAt)
            .Select(l => new DocumentLinkView(l.TargetKind, l.TargetId, l.AnchorItemId, l.Role, labels.GetValueOrDefault((l.TargetKind, l.TargetId), DeletedTargetLabel),
                DocumentRules.CanWriteLink(DocumentAccess.Facts(l), read.Actor)))
            .ToList();
        return new DocumentSummary(d.Id, d.Title, d.MediaType, d.SizeBytes, d.DocumentDate, d.UploadedAt, d.UploadedBy, d.ArchivedAt is not null, read.CanEdit, links, d.SourceRef);
    }

    /// <summary>Every item id the links name: a <c>core.item</c> target and every anchor.</summary>
    internal IEnumerable<ItemId> Items() => Links.Where(l => l.TargetKind == TargetKinds.CoreItem).Select(l => l.TargetId)
        .Concat(Links.Select(l => l.AnchorItemId).OfType<Guid>()).Distinct().Select(id => new ItemId(id));
}

public sealed record DocumentDetail(DocumentSummary Document, string OriginalFilename, string? Note, Guid UploadedBy) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Document.Items();
}

/// <summary>
/// Issue #420: one page of the documents (all of them when no page was asked), <c>Total</c> the number that matched the filters,
/// <c>Facets</c> what the filters offer over every document of the list before they apply.
/// </summary>
public sealed record DocumentList(IReadOnlyList<DocumentSummary> Items, int Total, DocumentFacets Facets) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.SelectMany(i => i.Items()).Concat(Facets.Links.SelectMany(l => l.Items()));
}

/// <summary>The link roles, the years (newest first) and the linked targets the listed documents carry, readable links only.</summary>
public sealed record DocumentFacets(IReadOnlyList<string> Roles, IReadOnlyList<int> Years, IReadOnlyList<DocumentLinkOption> Links);

public sealed record DocumentLinkOption(string TargetKind, Guid TargetId, Guid? AnchorItemId, string Label)
{
    internal IEnumerable<ItemId> Items() => new[] { TargetKind == TargetKinds.CoreItem ? TargetId : (Guid?)null, AnchorItemId }
        .OfType<Guid>().Distinct().Select(id => new ItemId(id));
}

/// <summary>The stored bytes, for the download endpoint only; the caller disposes <paramref name="Content"/>.</summary>
public sealed record DocumentContent(Stream Content, string MediaType, string FileName);

/// <summary>
/// Tranche 2 design §2.1: one target the page offers. <c>Module</c> and <c>ItemKind</c> are set for a <c>core.item</c> only;
/// <c>CanWrite</c> says whether the actor may link a document to it.
/// </summary>
public sealed record DocumentTargetView(string Kind, Guid Id, string Label, string? Module, string? ItemKind, bool CanWrite);

public sealed record DocumentTargetList(IReadOnlyList<DocumentTargetView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Where(t => t.Kind == TargetKinds.CoreItem).Select(t => new ItemId(t.Id));
}

public sealed record ReaderView(Guid Id, string Name);

/// <summary>The adults a link would let read the document who cannot today; never by which link the others read it.</summary>
public sealed record LinkPreview(IReadOnlyList<ReaderView> GainedReaders);
