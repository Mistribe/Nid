using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

/// <summary>
/// The preferences as the settings screen shows them. <paramref name="EmailAvailable"/>: SMTP is configured and the person has
/// an address, so the email switch can be turned on. <paramref name="HasEmail"/>: the person has an address, whatever the server.
/// </summary>
public sealed record PreferencesView(
    bool EmailEnabled, bool EmailAvailable, bool HasEmail, string Action, string Info, string Frequency, int Weekday,
    IReadOnlyList<LeadDayView> LeadDays, int Version);

/// <summary>One dated kind with the person's lead time and the kind's default.</summary>
public sealed record LeadDayView(string Kind, string Title, int Days, int DefaultDays);

public sealed record GetNotificationPreferencesQuery : IQuery<PreferencesView>;

/// <summary>Replaces the preferences. <paramref name="Version"/> is the one the caller read (0 when nothing was saved).</summary>
public sealed record SaveNotificationPreferencesCommand(
    bool EmailEnabled, ActionMode Action, InfoMode Info, DigestFrequency Frequency, int Weekday,
    IReadOnlyDictionary<string, int> LeadDays, int Version) : ICommand<PreferencesView>;

internal static class PreferencesViews
{
    public static async Task<PreferencesView> BuildAsync(
        NotificationPreferences prefs, NotificationCatalog catalog, IEmailAvailability email, IPersonRegistry persons, PersonId me, CancellationToken ct)
    {
        var person = await persons.FindAsync(me, ct);
        var hasEmail = !string.IsNullOrWhiteSpace(person?.Email);
        var available = email.IsConfigured && hasEmail;
        var leads = catalog.Kinds.Values.Where(k => k.LeadDays is not null).OrderBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => new LeadDayView(k.Kind, NotificationTemplates.TitleOf(k.Kind), prefs.Apply(k).LeadDays!.Value, k.LeadDays!.Value))
            .ToList();
        return new PreferencesView(
            prefs.EmailEnabled, available, hasEmail, prefs.Action.ToString().ToLowerInvariant(), prefs.Info.ToString().ToLowerInvariant(),
            prefs.Frequency.ToString().ToLowerInvariant(), (int)prefs.Weekday, leads, prefs.Version);
    }
}

[RequestHandler<GetNotificationPreferencesQuery, PreferencesView>]
public sealed class GetNotificationPreferencesHandler(
    INotificationStore store, NotificationCatalog catalog, IEmailAvailability email, IPersonRegistry persons, IActorAccessor accessor)
    : IRequestHandler<GetNotificationPreferencesQuery, PreferencesView>
{
    public async ValueTask<Result<PreferencesView>> HandleAsync(GetNotificationPreferencesQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<PreferencesView>(NotificationAccess.Unauthorized());

        var prefs = await store.PreferencesAsync(me, ct);
        return Result.Success(await PreferencesViews.BuildAsync(prefs, catalog, email, persons, me, ct));
    }
}

[RequestHandler<SaveNotificationPreferencesCommand, PreferencesView>]
public sealed class SaveNotificationPreferencesHandler(
    INotificationStore store, NotificationCatalog catalog, IEmailAvailability email, IPersonRegistry persons, IActorAccessor accessor)
    : IRequestHandler<SaveNotificationPreferencesCommand, PreferencesView>
{
    public async ValueTask<Result<PreferencesView>> HandleAsync(SaveNotificationPreferencesCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<PreferencesView>(NotificationAccess.Unauthorized());

        var errors = new List<FieldError>();
        var leads = new Dictionary<string, int>();
        foreach (var (kind, days) in command.LeadDays.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var path = $"leadDays.{kind}";
            if (catalog.Find(kind) is not { LeadDays: { } defaultDays })
                errors.Add(new FieldError(path, NotificationCodes.UnknownKind, "This notification has no lead time."));
            else if (days is < 0 or > NotificationPreferences.MaxLeadDays)
                errors.Add(new FieldError(path, NotificationCodes.InvalidLeadDays, $"A lead time is between 0 and {NotificationPreferences.MaxLeadDays} days."));
            else if (days != defaultDays)
                leads[kind] = days;
        }

        if (command.Weekday is < 1 or > 7)
            errors.Add(new FieldError("weekday", NotificationCodes.InvalidWeekday, "A weekday is 1 (Monday) to 7 (Sunday)."));

        if (command.EmailEnabled)
        {
            var person = await persons.FindAsync(me, ct);
            if (!email.IsConfigured || string.IsNullOrWhiteSpace(person?.Email))
                errors.Add(new FieldError("emailEnabled", NotificationCodes.EmailUnavailable, "Email needs a configured server and an address on your profile."));
        }

        if (errors.Count > 0)
            return Result.Failure<PreferencesView>(errors.Count == 1
                ? FieldErrors.At(new Failure(errors[0].Code, errors[0].Message, null), errors[0].Path)
                : FieldErrors.Shape(errors));

        var next = new NotificationPreferences(
            command.EmailEnabled, command.Action, command.Info, command.Frequency, (IsoDayOfWeek)command.Weekday, leads, command.Version);
        if (await store.SavePreferencesAsync(me, next, ct) is not { } saved)
            return Result.Failure<PreferencesView>(new Failure(NotificationCodes.PreferencesConflict,
                "Your preferences changed since you read them; read them again.", null));

        return Result.Success(await PreferencesViews.BuildAsync(saved, catalog, email, persons, me, ct));
    }
}
