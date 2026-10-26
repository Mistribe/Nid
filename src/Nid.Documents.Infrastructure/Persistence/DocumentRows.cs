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
