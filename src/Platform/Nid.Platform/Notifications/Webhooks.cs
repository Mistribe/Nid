using System.Text.RegularExpressions;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Notifications;

public static class WebhookStates
{
    public const string Ok = "ok";
    public const string Broken = "broken";
}

/// <summary>A webhook as the dispatcher needs it, decrypted. Never leaves the server.</summary>
public sealed record StoredWebhook(string Url, string Status);

/// <summary>What the API shows of a webhook: the last 4 characters of the URL, never the URL.</summary>
public sealed record WebhookView(string Hint, string Status);

/// <summary>Discord webhooks, encrypted at rest (notifications design §2.3). <c>owner</c> null is the household's.</summary>
public interface IWebhookStore
{
    Task<StoredWebhook?> FindAsync(PersonId? owner, CancellationToken ct);

    /// <summary>Upsert; the status goes back to <see cref="WebhookStates.Ok"/>.</summary>
    Task SetAsync(PersonId? owner, string url, CancellationToken ct);

    Task RemoveAsync(PersonId? owner, CancellationToken ct);

    /// <summary>
    /// Marks the webhook broken if it is still <paramref name="url"/>, the one the failed send used: a late failure of a replaced
    /// webhook leaves the new one alone. No-op without a row.
    /// </summary>
    Task MarkBrokenAsync(PersonId? owner, string url, CancellationToken ct);
}

public static partial class WebhookUrls
{
    [GeneratedRegex(@"^https://(discord\.com|discordapp\.com)/api/webhooks/[0-9]+/[A-Za-z0-9_-]+\z")]
    private static partial Regex Pattern();

    public static bool IsDiscord(string url) => Pattern().IsMatch(url);

    public static WebhookView? ViewOf(StoredWebhook? webhook)
        => webhook is null ? null : new WebhookView(webhook.Url.Length < 4 ? "" : webhook.Url[^4..], webhook.Status);
}

public sealed record SetDiscordWebhookCommand(string Url) : ICommand<WebhookView>;

public sealed record RemoveDiscordWebhookCommand : ICommand<NotificationsUpdated>;

public sealed record SetHouseholdWebhookCommand(string Url) : ICommand<WebhookView>, IAdminOnly;

public sealed record RemoveHouseholdWebhookCommand : ICommand<NotificationsUpdated>, IAdminOnly;

internal static class WebhookUseCases
{
    public static async Task<Result<WebhookView>> SetAsync(IWebhookStore store, PersonId? owner, string url, CancellationToken ct)
    {
        url = url.Trim();
        if (!WebhookUrls.IsDiscord(url))
            return Result.Failure<WebhookView>(FieldErrors.At(
                new Failure(NotificationCodes.InvalidWebhook, "Not a Discord webhook address.", null), "url"));
        await store.SetAsync(owner, url, ct);
        return Result.Success(WebhookUrls.ViewOf(new StoredWebhook(url, WebhookStates.Ok))!);
    }
}

[RequestHandler<SetDiscordWebhookCommand, WebhookView>]
public sealed class SetDiscordWebhookHandler(IWebhookStore store, IActorAccessor accessor)
    : IRequestHandler<SetDiscordWebhookCommand, WebhookView>
{
    public async ValueTask<Result<WebhookView>> HandleAsync(SetDiscordWebhookCommand command, CancellationToken ct = default)
        => accessor.Current?.PersonId is { } me
            ? await WebhookUseCases.SetAsync(store, me, command.Url, ct)
            : Result.Failure<WebhookView>(NotificationAccess.Unauthorized());
}

[RequestHandler<RemoveDiscordWebhookCommand, NotificationsUpdated>]
public sealed class RemoveDiscordWebhookHandler(IWebhookStore store, IActorAccessor accessor)
    : IRequestHandler<RemoveDiscordWebhookCommand, NotificationsUpdated>
{
    public async ValueTask<Result<NotificationsUpdated>> HandleAsync(RemoveDiscordWebhookCommand command, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<NotificationsUpdated>(NotificationAccess.Unauthorized());
        await store.RemoveAsync(me, ct);
        return Result.Success(new NotificationsUpdated());
    }
}

[RequestHandler<SetHouseholdWebhookCommand, WebhookView>]
public sealed class SetHouseholdWebhookHandler(IWebhookStore store) : IRequestHandler<SetHouseholdWebhookCommand, WebhookView>
{
    public async ValueTask<Result<WebhookView>> HandleAsync(SetHouseholdWebhookCommand command, CancellationToken ct = default)
        => await WebhookUseCases.SetAsync(store, null, command.Url, ct);
}

[RequestHandler<RemoveHouseholdWebhookCommand, NotificationsUpdated>]
public sealed class RemoveHouseholdWebhookHandler(IWebhookStore store) : IRequestHandler<RemoveHouseholdWebhookCommand, NotificationsUpdated>
{
    public async ValueTask<Result<NotificationsUpdated>> HandleAsync(RemoveHouseholdWebhookCommand command, CancellationToken ct = default)
    {
        await store.RemoveAsync(null, ct);
        return Result.Success(new NotificationsUpdated());
    }
}
