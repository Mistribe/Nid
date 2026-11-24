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
    IReadOnlyList<DocumentLinkView> Links, string? SourceRef, uint Version, DocumentMatch? Match = null)
{
    /// <summary>The label of a link whose target the resolvers no longer return; the link still counts through its anchor.</summary>
    public const string DeletedTargetLabel = "Élément supprimé";

    /// <summary><paramref name="labels"/>: from <see cref="DocumentAccess.LabelsAsync"/>.</summary>
    public static DocumentSummary Of(ReadableDocument read, IReadOnlyDictionary<(string Kind, Guid Id), string> labels, DocumentMatch? match = null)
    {
        var d = read.Document;
        var links = read.Links.Where(l => DocumentRules.CanReadLink(DocumentAccess.Facts(l, d.UploadedBy), read.Actor))
            .OrderBy(l => l.LinkedAt)
            .Select(l => new DocumentLinkView(l.TargetKind, l.TargetId, l.AnchorItemId, l.Role, labels.GetValueOrDefault((l.TargetKind, l.TargetId), DeletedTargetLabel),
                DocumentRules.CanWriteLink(DocumentAccess.Facts(l, d.UploadedBy), read.Actor)))
            .ToList();
        return new DocumentSummary(d.Id, d.Title, d.MediaType, d.SizeBytes, d.DocumentDate, d.UploadedAt, d.UploadedBy, d.ArchivedAt is not null, read.CanEdit, links, d.SourceRef, d.Version, match);
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
/// #169: with a search, <c>MeaningSearch</c> says whether the search by meaning took part (<see cref="MeaningSearchStates"/>).
public sealed record DocumentList(IReadOnlyList<DocumentSummary> Items, int Total, DocumentFacets Facets, string? MeaningSearch = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.SelectMany(i => i.Items()).Concat(Facets.Links.SelectMany(l => l.Items()));
}

/// <summary>
/// #169: where a search found a document in its text. <c>Snippet</c> is plain text; the words found sit between
/// <see cref="StartMark"/> and <see cref="EndMark"/>. <c>Ocr</c>: that page was read by OCR. <c>ByMeaning</c>: found by meaning only,
/// the snippet then being the start of the closest chunk.
/// </summary>
public sealed record DocumentMatch(string Snippet, int Page, bool Ocr, bool ByMeaning)
{
    public const string StartMark = "\uE000";
    public const string EndMark = "\uE001";
}

/// <summary>#169: <c>used</c> (the search by meaning took part), <c>unavailable</c> (no worker answered in time, or it was refused),
/// <c>off</c> (no <c>embed</c> model is set).</summary>
public static class MeaningSearchStates
{
    public const string Used = "used";
    public const string Unavailable = "unavailable";
    public const string Off = "off";
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
