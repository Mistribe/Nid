using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>How an action notification reaches the inbox (wire <c>immediate</c> | <c>digest</c>).</summary>
public enum ActionMode { Immediate, Digest }

/// <summary>How an info notification reaches the inbox (wire <c>digest</c> | <c>centre</c>: the centre only).</summary>
public enum InfoMode { Digest, Centre }

/// <summary>How often the digest is sent (wire <c>daily</c> | <c>weekly</c>).</summary>
public enum DigestFrequency { Daily, Weekly }

/// <summary>
/// A person's notification preferences (notifications design §2.3, §5.2). <paramref name="LeadDays"/> holds only the values that
/// differ from a kind's default. <paramref name="Version"/> is 0 until the first save.
/// </summary>
public sealed record NotificationPreferences(
    bool EmailEnabled, ActionMode Action, InfoMode Info, DigestFrequency Frequency, IsoDayOfWeek Weekday,
    IReadOnlyDictionary<string, int> LeadDays, int Version)
{
    public const int MaxLeadDays = 365;

    public static NotificationPreferences Defaults { get; }
        = new(false, ActionMode.Immediate, InfoMode.Digest, DigestFrequency.Weekly, IsoDayOfWeek.Monday, new Dictionary<string, int>(), 0);

    /// <summary>The kind with the person's lead time; an undated kind has none to override.</summary>
    public NotificationKind Apply(NotificationKind kind)
        => kind.LeadDays is not null && LeadDays.TryGetValue(kind.Kind, out var days) ? kind with { LeadDays = days } : kind;
}
