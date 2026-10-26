using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using Nid.Platform.Outbound;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Notifications;

/// <summary>
/// A Discord webhook channel (notifications design §5.3): <see cref="ChannelTarget.Address"/> is the webhook URL, a secret that
/// never reaches a log, a failure or <c>last_error</c>. One instance per channel name (<c>discord_person</c>, <c>discord_household</c>).
/// </summary>
public sealed partial class DiscordChannel(string name, SafeHttpClient http) : INotificationChannel
{
    private const int MaxTitle = 256;
    private const int MaxDescription = 4096;
    private const int MaxResponseBytes = 64 * 1024;

    public string Name => name;

    public bool IsAvailable => true;

    public async Task<Result> SendAsync(RenderedMessage message, ChannelTarget target, CancellationToken ct)
    {
        // An empty "parse" list: nothing in a title or a text (« @everyone », « <@123> ») can ping anybody.
        var payload = new Payload(
            [new Embed(Cut(message.Subject, MaxTitle), Cut(message.Text, MaxDescription))], new Mentions([]));
        try
        {
            var response = await http.PostJsonAsync(
                new Uri(target.Address), JsonSerializer.SerializeToUtf8Bytes(payload, DiscordJson.Default.Payload), MaxResponseBytes, ct);
            var status = (int)response.Status;
            return status is >= 200 and < 300
                ? Result.Success()
                : status is 401 or 403 or 404 ? Fail(NotificationChannelCodes.Broken, status)
                : status == 429 || status >= 500 ? Fail(NotificationChannelCodes.Unreachable, status)
                : Fail(NotificationChannelCodes.Rejected, status);
        }
        catch (OutboundRefusedException)
        {
            // The operator's host list lacks Discord (or the URL is not one): retrying changes nothing.
            return Fail(NotificationChannelCodes.Rejected, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(NotificationChannelCodes.Unreachable, null);
        }
    }

    /// <summary>Cut to <paramref name="max"/> characters, the last one « … ».</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var end = char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1; // never leave half of a pair
        return text[..end] + "…";
    }

    // No URL, subject or text: the code and the status are all that is kept.
    private static Result Fail(string code, int? status)
        => Result.Failure(new Failure(
            code, code, status is { } s ? new Dictionary<string, object?> { [NotificationChannelCodes.StatusKey] = s } : null));

    internal sealed record Payload(
        [property: JsonPropertyName("embeds")] Embed[] Embeds,
        [property: JsonPropertyName("allowed_mentions")] Mentions AllowedMentions);

    internal sealed record Embed(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description);

    internal sealed record Mentions([property: JsonPropertyName("parse")] string[] Parse);

    [JsonSerializable(typeof(Payload))]
    internal sealed partial class DiscordJson : JsonSerializerContext;
}
