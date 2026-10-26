using Nid.Platform.Notifications;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using NodaTime;
using IResult = Microsoft.AspNetCore.Http.IResult;

namespace Nid.Host.Api;

/// <summary>The notification centre (notifications design §4): what to do, what is put off, what is over.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/platform/notifications").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/", async (string? list, Guid? before, int? limit, IInvoker invoker, CancellationToken ct) =>
            {
                NotificationList? which = (list ?? "todo").ToLowerInvariant() switch
                {
                    "todo" => NotificationList.Todo,
                    "snoozed" => NotificationList.Snoozed,
                    "history" => NotificationList.History,
                    _ => null,
                };
                if (which is null)
                {
                    var reader = new FieldReader();
                    reader.Add("list", "request.invalid", "todo, snoozed or history.");
                    return reader.Problem();
                }
                return (await invoker.InvokeAsync(new ListNotificationsQuery(which.Value, before, limit), ct)).ToHttp(p => p);
            })
            .Returns<NotificationPage>();

        api.MapGet("/count", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new CountNotificationsQuery(), ct)).ToHttp(c => c))
            .Returns<NotificationCount>();

        api.MapPost("/read-all", async (IInvoker invoker, CancellationToken ct)
                => NoContent(await invoker.InvokeAsync(new ReadAllNotificationsCommand(), ct)))
            .ReturnsNoContent();

        api.MapPost("/{id:guid}/read", async (Guid id, IInvoker invoker, CancellationToken ct)
                => NoContent(await invoker.InvokeAsync(new ReadNotificationCommand(id), ct)))
            .ReturnsNoContent();

        api.MapPost("/{id:guid}/dismiss", async (Guid id, IInvoker invoker, CancellationToken ct)
                => NoContent(await invoker.InvokeAsync(new DismissNotificationCommand(id), ct)))
            .ReturnsNoContent();

        api.MapPost("/{id:guid}/snooze", async (Guid id, SnoozeBody? body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                LocalDate? until = body?.Until is null ? null : reader.Date(body.Until, "until");
                SnoozePreset? preset = null;
                if (body?.Preset is { } text)
                {
                    if (Enum.TryParse<SnoozePreset>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                        preset = parsed;
                    else
                        reader.Add("preset", "request.invalid", "week or month.");
                }
                if (!reader.Ok)
                    return reader.Problem();
                return NoContent(await invoker.InvokeAsync(new SnoozeNotificationCommand(id, until, preset), ct));
            })
            .ReturnsNoContent();

        api.MapGet("/preferences", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetNotificationPreferencesQuery(), ct)).ToHttp(p => p))
            .Returns<PreferencesView>();

        api.MapPut("/preferences", async (NotificationPreferencesBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var action = body.Action?.ToLowerInvariant() switch
                {
                    "immediate" => ActionMode.Immediate,
                    "digest" => ActionMode.Digest,
                    _ => (ActionMode?)null,
                };
                var info = body.Info?.ToLowerInvariant() switch
                {
                    "digest" => InfoMode.Digest,
                    "centre" => InfoMode.Centre,
                    _ => (InfoMode?)null,
                };
                var frequency = body.Frequency?.ToLowerInvariant() switch
                {
                    "daily" => DigestFrequency.Daily,
                    "weekly" => DigestFrequency.Weekly,
                    _ => (DigestFrequency?)null,
                };
                if (action is null)
                    reader.Add("action", "request.invalid", "immediate or digest.");
                if (info is null)
                    reader.Add("info", "request.invalid", "digest or centre.");
                if (frequency is null)
                    reader.Add("frequency", "request.invalid", "daily or weekly.");
                if (!reader.Ok)
                    return reader.Problem();

                var command = new SaveNotificationPreferencesCommand(
                    body.EmailEnabled, action!.Value, info!.Value, frequency!.Value, body.Weekday,
                    body.LeadDays ?? new Dictionary<string, int>(), body.Version);
                return (await invoker.InvokeAsync(command, ct)).ToHttp(p => p);
            })
            .Returns<PreferencesView>();

        api.MapPost("/test", async (TestChannelBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new TestChannelCommand(body.Channel ?? ""), ct)).ToHttp(r => r))
            .Returns<TestChannelResult>();

        return routes;
    }

    // A 204 carries no body: ToHttp would write one.
    private static IResult NoContent(Result<NotificationsUpdated> result)
        => result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);

    public sealed record TestChannelBody(string? Channel);

    public sealed record SnoozeBody(string? Until, string? Preset);

    public sealed record NotificationPreferencesBody(
        bool EmailEnabled, string? Action, string? Info, string? Frequency, int Weekday, Dictionary<string, int>? LeadDays, int Version);
}
