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
    IReadOnlyList<LeadDayView> LeadDays, IReadOnlyList<SignalView> Signals, int Version, WebhookView? Discord);

/// <summary>One dated kind with the person's lead time and the kind's default.</summary>
public sealed record LeadDayView(string Kind, string Title, int Days, int DefaultDays);

/// <summary>Issue #81: one opt-in kind, whether the person turned it on, its threshold (the default while off) and its bounds.</summary>
public sealed record SignalView(string Kind, string Title, bool Enabled, decimal Threshold, decimal DefaultThreshold, decimal Min, decimal Max, string Unit);

public sealed record GetNotificationPreferencesQuery : IQuery<PreferencesView>;

/// <summary>Replaces the preferences. <paramref name="Version"/> is the one the caller read (0 when nothing was saved).</summary>
public sealed record SaveNotificationPreferencesCommand(
    bool EmailEnabled, ActionMode Action, InfoMode Info, DigestFrequency Frequency, int Weekday,
    IReadOnlyDictionary<string, int> LeadDays, IReadOnlyDictionary<string, decimal> Signals, int Version) : ICommand<PreferencesView>;

internal static class PreferencesViews
{
    public static async Task<PreferencesView> BuildAsync(
        NotificationPreferences prefs, NotificationCatalog catalog, IEmailAvailability email, IPersonRegistry persons, IWebhookStore webhooks, PersonId me, CancellationToken ct)
    {
        var person = await persons.FindAsync(me, ct);
        var hasEmail = !string.IsNullOrWhiteSpace(person?.Email);
        var available = email.IsConfigured && hasEmail;
        var leads = catalog.Kinds.Values.Where(k => k.LeadDays is not null).OrderBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => new LeadDayView(k.Kind, NotificationTemplates.TitleOf(k.Kind), prefs.Apply(k).LeadDays!.Value, k.LeadDays!.Value))
            .ToList();
        var signals = catalog.Kinds.Values.Where(k => k.OptIn).OrderBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => new SignalView(k.Kind, NotificationTemplates.TitleOf(k.Kind), prefs.Signals.ContainsKey(k.Kind),
                prefs.Signals.GetValueOrDefault(k.Kind, k.Threshold!.Default), k.Threshold.Default, k.Threshold.Min, k.Threshold.Max, k.Threshold.Unit))
            .ToList();
        return new PreferencesView(
            prefs.EmailEnabled, available, hasEmail, prefs.Action.ToString().ToLowerInvariant(), prefs.Info.ToString().ToLowerInvariant(),
            prefs.Frequency.ToString().ToLowerInvariant(), (int)prefs.Weekday, leads, signals, prefs.Version,
            WebhookUrls.ViewOf(await webhooks.FindAsync(me, ct)));
    }
}

[RequestHandler<GetNotificationPreferencesQuery, PreferencesView>]
public sealed class GetNotificationPreferencesHandler(
    INotificationStore store, NotificationCatalog catalog, IEmailAvailability email, IPersonRegistry persons, IWebhookStore webhooks, IActorAccessor accessor)
    : IRequestHandler<GetNotificationPreferencesQuery, PreferencesView>
{
    public async ValueTask<Result<PreferencesView>> HandleAsync(GetNotificationPreferencesQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<PreferencesView>(NotificationAccess.Unauthorized());

        var prefs = await store.PreferencesAsync(me, ct);
        return Result.Success(await PreferencesViews.BuildAsync(prefs, catalog, email, persons, webhooks, me, ct));
    }
}

[RequestHandler<SaveNotificationPreferencesCommand, PreferencesView>]
public sealed class SaveNotificationPreferencesHandler(
    INotificationStore store, NotificationCatalog catalog, IEmailAvailability email, IPersonRegistry persons, IWebhookStore webhooks, IActorAccessor accessor)
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

        var turnedOn = new Dictionary<string, decimal>();
        foreach (var (kind, threshold) in command.Signals.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var path = $"signals.{kind}";
            if (catalog.Find(kind)?.Threshold is not { } bounds)
                errors.Add(new FieldError(path, NotificationCodes.UnknownKind, "This notification cannot be turned on."));
            else if (threshold < bounds.Min || threshold > bounds.Max)
                errors.Add(new FieldError(path, NotificationCodes.InvalidThreshold, $"A threshold is between {bounds.Min} and {bounds.Max}."));
            else
                turnedOn[kind] = threshold;
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
            command.EmailEnabled, command.Action, command.Info, command.Frequency, (IsoDayOfWeek)command.Weekday, leads, turnedOn, command.Version);
        if (await store.SavePreferencesAsync(me, next, ct) is not { } saved)
            return Result.Failure<PreferencesView>(new ConflictFailure(
                "Your preferences changed since you read them; read them again.") { Code = NotificationCodes.PreferencesConflict });

        return Result.Success(await PreferencesViews.BuildAsync(saved, catalog, email, persons, webhooks, me, ct));
    }
}

/// <summary>Issue #81: the opt-in kinds the person turned on, read from their saved preferences.</summary>
public sealed class SignalSettings(INotificationStore store) : ISignalSettings
{
    public async Task<IReadOnlyDictionary<string, decimal>> ForAsync(PersonId reader, CancellationToken ct)
        => (await store.PreferencesAsync(reader, ct)).Signals;
}
