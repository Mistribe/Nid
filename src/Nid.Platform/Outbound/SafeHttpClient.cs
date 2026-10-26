using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Nid.Platform.Outbound;

public sealed class OutboundOptions
{
    /// <summary>
    /// An array, not a list: configuration binding replaces an array but appends to a pre-filled list. The catalog and
    /// the ECB (1C2a), and the hosts of the catalog's icons (1C2c design §5), the two price sources (holding prices spec §2), INSEE (rental spec §3) and Discord webhooks (notifications design §5.3): an icon anywhere else is refused
    /// <c>host_not_allowed</c> until the operator adds its host here.
    /// </summary>
    public string[] AllowedHosts { get; set; } =
        ["raw.githubusercontent.com", "www.ecb.europa.eu", "1000logos.net", "upload.wikimedia.org", "www.nounou-top.fr", "www.boursorama.com", "query2.finance.yahoo.com", "bdm.insee.fr", "discord.com", "discordapp.com"];

    /// <summary>Sent on every request: <c>upload.wikimedia.org</c> refuses a request without one (403).</summary>
    public string UserAgent { get; set; } = "Nid/1.0 (+https://github.com/Mistribe/Nid)";

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRedirects { get; set; } = 3;
}

public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

public sealed class OutboundRefusedException(string reason, string host)
    : Exception($"Outbound request to '{host}' refused: {reason}.")
{
    public string Reason { get; } = reason;

    public string Host { get; } = host;
}

public sealed record OutboundResponse(HttpStatusCode Status, byte[] Body);

public static class OutboundHandler
{
    /// <summary>
    /// Resolves the name itself, refuses it if any answer is a refused address, and connects to the addresses it
    /// checked — so a second, different DNS answer (rebinding) is never what the connection uses. No proxy: a proxy
    /// would make the connection to an address this check never sees.
    /// </summary>
    public static SocketsHttpHandler Create(IHostResolver resolver) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await resolver.ResolveAsync(host, ct);
            if (addresses.Length == 0 || !addresses.All(AddressPolicy.IsAllowed))
                throw new OutboundRefusedException("address_refused", host);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>
/// The single outbound HTTP client of <c>nid-api</c> (<c>security/application.md</c> §3, D21): HTTPS only, explicit
/// host list, every redirect revalidated, bounded time and size, each refusal logged with host and reason only.
/// </summary>
public sealed partial class SafeHttpClient(HttpMessageInvoker invoker, OutboundOptions options, ILogger<SafeHttpClient> logger)
{
    public async Task<OutboundResponse> GetAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var current = url;
        for (var hop = 0; ; hop++)
        {
            Check(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            HttpResponseMessage response;
            try
            {
                response = await invoker.SendAsync(request, timeout.Token);
            }
            catch (HttpRequestException e) when (RefusalIn(e) is { } refused)
            {
                throw Refuse(refused.Reason, refused.Host);
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (hop >= options.MaxRedirects)
                        throw Refuse("too_many_redirects", current.Host);
                    if (response.Headers.Location is not { } location)
                        throw Refuse("redirect_without_location", current.Host);
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                    throw Refuse("too_large", current.Host);

                return new OutboundResponse(response.StatusCode, await ReadBoundedAsync(response.Content, maxBytes, current.Host, timeout.Token));
            }
        }
    }

    /// <summary>
    /// One POST with the same checks as <see cref="GetAsync"/>. A redirect is refused, never followed: a POST must not be replayed
    /// elsewhere, and the webhook URL is a secret.
    /// </summary>
    public async Task<OutboundResponse> PostJsonAsync(Uri url, byte[] json, int maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        Check(url);
        using var content = new ByteArrayContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        HttpResponseMessage response;
        try
        {
            response = await invoker.SendAsync(request, timeout.Token);
        }
        catch (HttpRequestException e) when (RefusalIn(e) is { } refused)
        {
            throw Refuse(refused.Reason, refused.Host);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
                throw Refuse("redirect_on_post", url.Host);
            if (response.Content.Headers.ContentLength > maxBytes)
                throw Refuse("too_large", url.Host);

            return new OutboundResponse(response.StatusCode, await ReadBoundedAsync(response.Content, maxBytes, url.Host, timeout.Token));
        }
    }

    private void Check(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            throw Refuse("not_https", url.IsAbsoluteUri ? url.Host : "");
        if (!string.IsNullOrEmpty(url.UserInfo))
            throw Refuse("user_info", url.Host);
        if (!options.AllowedHosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw Refuse("host_not_allowed", url.Host);
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, string host, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw Refuse("too_large", host);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static OutboundRefusedException? RefusalIn(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
            if (inner is OutboundRefusedException refused)
                return refused;
        return null;
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private OutboundRefusedException Refuse(string reason, string host)
    {
        LogRefused(host, reason);
        return new OutboundRefusedException(reason, host);
    }

    // Host and reason only: a query string could carry something that is not ours to log.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbound request to {Host} refused: {Reason}")]
    private partial void LogRefused(string host, string reason);
}
