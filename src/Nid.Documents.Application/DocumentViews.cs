using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using NodaTime;

namespace Nid.Documents.Application;

public sealed record DocumentLinkView(string TargetKind, Guid TargetId, string Role, string Label);

/// <summary>Design §7: <c>Links</c> holds only the links whose target the reader reads; the others are absent, not counted.</summary>
public sealed record DocumentSummary(
    Guid Id, string Title, string MediaType, long SizeBytes, LocalDate? DocumentDate, Instant UploadedAt, bool Archived, bool CanEdit,
    IReadOnlyList<DocumentLinkView> Links)
{
    /// <summary><paramref name="labels"/>: from <see cref="DocumentAccess.LabelsAsync"/>.</summary>
    public static DocumentSummary Of(ReadableDocument read, IReadOnlyDictionary<(string Kind, Guid Id), string> labels)
    {
        var d = read.Document;
        var links = read.Links.Where(l => DocumentRules.CanReadLink(DocumentAccess.Facts(l), read.Actor))
            .OrderBy(l => l.LinkedAt)
            .Select(l => new DocumentLinkView(l.TargetKind, l.TargetId, l.Role, labels.GetValueOrDefault((l.TargetKind, l.TargetId), "")))
            .ToList();
        return new DocumentSummary(d.Id, d.Title, d.MediaType, d.SizeBytes, d.DocumentDate, d.UploadedAt, d.ArchivedAt is not null, read.CanEdit, links);
    }

    internal IEnumerable<ItemId> Items() => Links.Where(l => l.TargetKind == TargetKinds.CoreItem).Select(l => new ItemId(l.TargetId));
}

public sealed record DocumentDetail(DocumentSummary Document, string OriginalFilename, string? Note, Guid UploadedBy) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Document.Items();
}

public sealed record DocumentList(IReadOnlyList<DocumentSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.SelectMany(i => i.Items());
}

/// <summary>The stored bytes, for the download endpoint only; the caller disposes <paramref name="Content"/>.</summary>
public sealed record DocumentContent(Stream Content, string MediaType, string FileName);
