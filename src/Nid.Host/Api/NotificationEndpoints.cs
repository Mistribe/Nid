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

        return routes;
    }

    // A 204 carries no body: ToHttp would write one.
    private static IResult NoContent(Result<NotificationsUpdated> result)
        => result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);

    public sealed record SnoozeBody(string? Until, string? Preset);
}
