using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;
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
/// signal's due date it starts to show; none means it shows as soon as it is seen. <paramref name="Threshold"/> (issue #81): the
/// kind is opt-in, off until the person turns it on with a threshold in their preferences.
/// </summary>
public sealed record NotificationKind(string Kind, string Family, NotificationLevel Level, string Module, int? LeadDays, SignalThreshold? Threshold = null)
{
    public bool OptIn => Threshold is not null;

    /// <summary>Issue #514: how a notification of the kind reads, declared with it by its module; none reads as the kind's name, with no body.</summary>
    public NotificationTemplate? Template { get; init; }

    /// <summary>The kind tells one person's business (a token, a decision of theirs): never for the household, with or without an item.</summary>
    public bool Personal { get; init; }
}

/// <summary>
/// Issue #514: the French copy of a kind (notifications design §4), next to the kind in its module. The title names the kind; a row about an
/// item reads its name then <see cref="Suffix"/>, a row about none (or a <see cref="Notice"/>) reads <see cref="Body"/>. No amount: the
/// figures a signal was raised on are added by the centre.
/// </summary>
public sealed record NotificationTemplate(string Title)
{
    /// <summary>A notice says what happened, not about what: never an item's name, its <see cref="Body"/> whatever its items.</summary>
    public bool Notice { get; init; }

    /// <summary>The text of a row about no item or of a notice; empty when not given.</summary>
    public Func<NotificationFacts, string>? Body { get; init; }

    /// <summary>What follows the item's name; by default the due date (<see cref="NotificationFacts.OnDueDate"/>).</summary>
    public Func<NotificationFacts, string>? Suffix { get; init; }

    /// <summary>The screen the notification opens, a path; « / » when not given.</summary>
    public Func<NotificationFacts, string>? Link { get; init; }
}

/// <summary>Issue #514: what a template reads of a notification; <paramref name="Item"/> its first item, null for a row about none.</summary>
public sealed record NotificationFacts(string Kind, string Key, ItemId? Item, LocalDate? DueOn, IReadOnlyDictionary<string, string> Params)
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>« 1er décembre 2027 », « 31 mars 2027 ».</summary>
    public static string Day(LocalDate date)
        => (date.Day == 1 ? "1er" : date.Day.ToString(French)) + " " + date.ToString("MMMM yyyy", French);

    /// <summary>« — le 1er mars 2027 » with a due date, nothing without: the suffix of a kind that says none.</summary>
    public string OnDueDate => DueOn is { } on ? $" — le {Day(on)}" : "";

    /// <summary>The date the key ends with (<see cref="SignalKeys"/>): the month of arrears, the day of a valuation.</summary>
    public LocalDate? KeyDate(string format)
    {
        var last = Key[(Key.LastIndexOf(':') + 1)..];
        return LocalDatePattern.CreateWithInvariantCulture(format).Parse(last) is { Success: true } parsed ? parsed.Value : null;
    }

    /// <summary><paramref name="path"/> for a row about no item, « / » for one about an item.</summary>
    public string HouseholdPage(string path) => Item is null ? path : "/";

    /// <summary>The item's page, <c>/{page}/{id}</c> with its tab when given; « / » for a row about no item.</summary>
    public string ItemPage(string page, string? tab = null)
        => Item is { } item ? $"/{page}/{item.Value:D}{(tab is null ? "" : $"?tab={tab}")}" : "/";
}

/// <summary>Issue #81: an opt-in kind's threshold, its default and bounds, in <paramref name="Unit"/> (<c>eur</c>, <c>pct</c>, <c>months</c>).</summary>
public sealed record SignalThreshold(decimal Default, decimal Min, decimal Max, string Unit);

/// <summary>Issue #81: the opt-in kinds a reader turned on, with their thresholds; a kind absent is off. A source reads it first.</summary>
public interface ISignalSettings
{
    Task<IReadOnlyDictionary<string, decimal>> ForAsync(PersonId reader, CancellationToken ct);
}

/// <summary>
/// One thing a module says deserves attention, for one reader (notifications design §2.2). <paramref name="Key"/> identifies
/// the occurrence, see <see cref="SignalKeys"/>. Issue #81: amounts go in <paramref name="Params"/> under keys ending in
/// <c>_amount</c> (invariant decimals, in the reference currency); only the centre shows them.
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

/// <summary>
/// #644: the kinds a module publishes through <see cref="INotifier"/>, which no source evaluates, declared with their copy next to their owner.
/// The catalogue reads every one the host registers, next to the sources' kinds.
/// </summary>
public interface IPublishedNotificationKinds
{
    IReadOnlyList<NotificationKind> Kinds { get; }
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
    public const string InvalidThreshold = "notifications.invalid_threshold";
    public const string InvalidWeekday = "notifications.invalid_weekday";
    public const string EmailUnavailable = "notifications.email_unavailable";
    public const string PreferencesConflict = "notifications.preferences_conflict";
    public const string UnknownChannel = "notifications.unknown_channel";
    public const string InvalidWebhook = "notifications.invalid_webhook";
    public const string DiscordUnavailable = "notifications.discord_unavailable";

    /// <summary>A test message on the same channel less than 30 seconds ago: answered 429, the channel is not called.</summary>
    public const string TestThrottled = "notifications.test_throttled";
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

/// <summary>The families of the notices published through <see cref="INotifier"/> (notifications design §2.1).</summary>
public static class NotificationFamilies
{
    public const string Security = "security";
    public const string Action = "action";
    public const string Health = "health";
}

/// <summary>Who a published notice is for. Dependents and pending adults never receive one.</summary>
public abstract record Recipients
{
    /// <summary>These people, when they are active adults: several deciders, or one id for a holder.</summary>
    public sealed record People(IReadOnlyList<PersonId> Ids) : Recipients;

    public sealed record AllAdults : Recipients;

    public sealed record Admins : Recipients;
}

/// <summary>One occurrence of a kind published by code, not by an evaluation: <paramref name="Key"/> identifies the occurrence.</summary>
public sealed record Notice(string Kind, Recipients Recipients, string Key, IReadOnlyList<ItemId> ItemIds, IReadOnlyDictionary<string, string> Params);

/// <summary>Publishes and resolves one-off notices (notifications design §2.1). Neither call ever throws: a failure is logged.</summary>
public interface INotifier
{
    Task PublishAsync(Notice notice, CancellationToken ct);

    Task ResolveAsync(string key, CancellationToken ct);
}
