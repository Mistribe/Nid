using NodaTime;

namespace Nid.Documents.Infrastructure.Persistence;

public sealed class DocumentRow
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string MediaType { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string Title { get; set; } = "";
    public LocalDate? DocumentDate { get; set; }
    public string? Note { get; set; }
    public Guid UploadedByPersonId { get; set; }
    public Instant UploadedAt { get; set; }
    public Instant? ArchivedAt { get; set; }
    public string? SourceRef { get; set; }
}

public sealed class DocumentLinkRow
{
    public Guid DocumentId { get; set; }
    public string TargetKind { get; set; } = "";
    public Guid TargetId { get; set; }
    public Guid? AnchorItemId { get; set; }
    public string Role { get; set; } = "";
    public Guid LinkedByPersonId { get; set; }
    public Instant LinkedAt { get; set; }
}

/// <summary>Issue #414: a document marked « pas disponible »: that period of that role on that target is no longer asked for.</summary>
public sealed class ExpectedDismissalRow
{
    public string TargetKind { get; set; } = "";
    public Guid TargetId { get; set; }
    public string Role { get; set; } = "";
    public int Period { get; set; }
    public Guid DismissedByPersonId { get; set; }
    public Instant DismissedAt { get; set; }
}

/// <summary>#305: an upload URL not used yet; no history, it is deleted when claimed or expired.</summary>
public sealed class UploadTicketRow
{
    public string TokenHash { get; set; } = "";
    public Guid PersonId { get; set; }
    public Guid TokenId { get; set; }
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";
    public LocalDate DocumentDate { get; set; }
    public string? Note { get; set; }
    public string TargetKind { get; set; } = "";
    public Guid TargetId { get; set; }
    public string Role { get; set; } = "";
    public string? SourceRef { get; set; }
    public Instant ExpiresAt { get; set; }
}
