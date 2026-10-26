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

/// <summary>One notification for one person, keyed by (person, key) (notifications design §2.3).</summary>
public sealed class NotificationRow
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string Level { get; set; } = "";
    public Guid[] ItemIds { get; set; } = [];
    public LocalDate? DueOn { get; set; }
    public LocalDate NotifyOn { get; set; }

    /// <summary>The signal's parameters as a JSON object of strings.</summary>
    public string Params { get; set; } = "{}";

    public string State { get; set; } = "pending";
    public Instant? ReadAt { get; set; }
    public LocalDate? SnoozedUntil { get; set; }
    public Instant? DigestedAt { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? OpenedAt { get; set; }
    public Instant? ResolvedAt { get; set; }
    public Instant? DismissedAt { get; set; }
}

/// <summary>The last evaluation of one source for one person: what the daily catch-up and the 15-minute refresh read.</summary>
public sealed class NotificationRunRow
{
    public Guid PersonId { get; set; }
    public string Source { get; set; } = "";
    public LocalDate RanOn { get; set; }
    public Instant RanAt { get; set; }
    public bool Ok { get; set; }
    public LocalDate? LastOkOn { get; set; }
}
