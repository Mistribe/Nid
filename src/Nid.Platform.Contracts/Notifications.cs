using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Platform.Contracts;

/// <summary>How loudly a notification speaks (notifications design §2.2).</summary>
public enum NotificationLevel
{
    Critical,
    Action,
    Info,
}

/// <summary>
/// A kind of notification a module declares (notifications design §2.2). <paramref name="LeadDays"/> is how long before a
/// signal's due date it starts to show; none means it shows as soon as it is seen.
/// </summary>
public sealed record NotificationKind(string Kind, string Family, NotificationLevel Level, string Module, int? LeadDays);

/// <summary>
/// One thing a module says deserves attention, for one reader (notifications design §2.2). <paramref name="Key"/> identifies
/// the occurrence, see <see cref="SignalKeys"/>.
/// </summary>
public sealed record Signal(string Key, string Kind, IReadOnlyList<ItemId> ItemIds, LocalDate? DueOn, IReadOnlyDictionary<string, string> Params);

/// <summary>A module's contribution to the notification centre (notifications design §2.2).</summary>
public interface INotificationSource
{
    /// <summary>The module's name, the key of its <c>notification_run</c> row.</summary>
    string Name { get; }

    IReadOnlyList<NotificationKind> Kinds { get; }

    Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct);
}

/// <summary>The states of a stored notification (notifications design §3.3).</summary>
public static class NotificationStates
{
    public const string Pending = "pending";
    public const string Open = "open";
    public const string Snoozed = "snoozed";
    public const string Dismissed = "dismissed";
    public const string Resolved = "resolved";
}

/// <summary>Failure codes of the notification commands (notifications design §4).</summary>
public static class NotificationCodes
{
    public const string NotSnoozable = "notifications.not_snoozable";
    public const string NotDismissable = "notifications.not_dismissable";
    public const string InvalidSnooze = "notifications.invalid_snooze";
    public const string UnknownKind = "notifications.unknown_kind";
    public const string InvalidLeadDays = "notifications.invalid_lead_days";
    public const string InvalidWeekday = "notifications.invalid_weekday";
    public const string EmailUnavailable = "notifications.email_unavailable";
    public const string PreferencesConflict = "notifications.preferences_conflict";
    public const string UnknownChannel = "notifications.unknown_channel";
    public const string InvalidWebhook = "notifications.invalid_webhook";
    public const string DiscordUnavailable = "notifications.discord_unavailable";
}

/// <summary>
/// The key of a signal carries the occurrence it is about, so an occurrence that returns later is a new key (notifications
/// design §3.3).
/// </summary>
public static class SignalKeys
{
    public static string Dated(string kind, ItemId item, LocalDate on)
        => $"{kind}:{item.Value:D}:{on:yyyy-MM-dd}";

    public static string Monthly(string kind, ItemId item, LocalDate today)
        => $"{kind}:{item.Value:D}:{today:yyyy-MM}";

    /// <summary>A signal about the household and no item (a missing rate, the month's routine): the discriminator tells occurrences apart.</summary>
    public static string Household(string kind, string discriminator)
        => $"{kind}:{discriminator}";

    public static string Undated(string kind, ItemId item)
        => $"{kind}:{item.Value:D}";
}
