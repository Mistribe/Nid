using NodaTime;

namespace Nid.Platform.Infrastructure.Persistence;

public sealed class ImportSessionRow
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string State { get; set; } = "open";
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
    public Instant ExpiresAt { get; set; }

    /// <summary>File ref → person id, as JSON: the adult map given at session creation.</summary>
    public string Adults { get; set; } = "{}";

    public string? Report { get; set; }
}

public sealed class ImportBatchRow
{
    public Guid SessionId { get; set; }
    public string Section { get; set; } = "";
    public int BatchIndex { get; set; }
    public string ContentHash { get; set; } = "";
    public int EntityCount { get; set; }
    public string Payload { get; set; } = "[]";
    public Instant ReceivedAt { get; set; }
}
