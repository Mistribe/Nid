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

    /// <summary>The digest that carried this row, set with <c>DigestedAt</c>; the dispatcher lists a digest's rows by it.</summary>
    public Guid? DigestId { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? OpenedAt { get; set; }
    public Instant? ResolvedAt { get; set; }
    public Instant? DismissedAt { get; set; }

    /// <summary>1 at the first opening; a wake after a snooze or a reopen of a resolved key opens a new round.</summary>
    public int Round { get; set; } = 1;
}

/// <summary>
/// One message to send on one channel for one opening (round) of a notification (notifications design §2.3, §5.1). Not audited:
/// it is the outbox, not data. <c>LastError</c> holds a code, never content.
/// </summary>
public sealed class NotificationDeliveryRow
{
    public Guid Id { get; set; }

    /// <summary>The notification of a single message; null for a digest.</summary>
    public Guid? NotificationId { get; set; }

    /// <summary>The digest of a grouped message; null for a single one.</summary>
    public Guid? DigestId { get; set; }

    public Guid PersonId { get; set; }
    public string Channel { get; set; } = "";
    public int Round { get; set; } = 1;

    /// <summary>pending | sent | failed | skipped.</summary>
    public string State { get; set; } = "pending";

    /// <summary>How many sends were tried.</summary>
    public int Attempts { get; set; }

    public Instant? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? SentAt { get; set; }

    /// <summary>The notification key of a household message: one per key, ever. Null for any other message.</summary>
    public string? HouseholdKey { get; set; }
}

/// <summary>
/// A person's digest for one period (<c>yyyy-MM-dd</c> daily, ISO <c>yyyy-Www</c> weekly). Not audited: it is the outbox. The unique
/// (person, period) decides which run sends it.
/// </summary>
public sealed class NotificationDigestRow
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string PeriodKey { get; set; } = "";
    public Instant CreatedAt { get; set; }
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

/// <summary>A person's notification preferences (notifications design §2.3). Audited: it is a setting.</summary>
public sealed class NotificationPreferencesRow
{
    public Guid PersonId { get; set; }
    public bool EmailEnabled { get; set; }
    public string ActionMode { get; set; } = "immediate";
    public string InfoMode { get; set; } = "digest";
    public string Frequency { get; set; } = "weekly";

    /// <summary>ISO day of week, 1 (Monday) to 7.</summary>
    public int Weekday { get; set; } = 1;

    /// <summary>A JSON object of kind to days, only the values that differ from the kind's default.</summary>
    public string LeadDays { get; set; } = "{}";

    /// <summary>Optimistic concurrency: the saved version, 1 for the first save.</summary>
    public int Version { get; set; }
}

/// <summary>
/// A Discord webhook: a person's, or the household's when <see cref="PersonId"/> is null (notifications design §2.3). Audited
/// (a setting); the audit stores the encrypted bytes at most. No version: the last write wins.
/// </summary>
public sealed class NotificationWebhookRow
{
    public Guid Id { get; set; }
    public Guid? PersonId { get; set; }

    /// <summary>The URL, protected by Data Protection.</summary>
    public byte[] Url { get; set; } = [];

    public string Status { get; set; } = "ok";
    public Instant UpdatedAt { get; set; }
}
