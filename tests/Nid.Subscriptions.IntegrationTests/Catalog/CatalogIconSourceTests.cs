using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Platform.Outbound;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>One icon over the real <see cref="SafeHttpClient"/> behind a stub handler: no network, the default hosts.</summary>
public class CatalogIconSourceTests
{
    private const string Logo = "https://1000logos.net/wp-content/uploads/netflix.png";

    private sealed class StubHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] answers) : HttpMessageHandler
    {
        private int next;

        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return answers[Math.Min(next++, answers.Length - 1)](request, cancellationToken);
        }
    }

    private sealed class FixedResolver(string address) : IHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse(address) });
    }

    private static CatalogIconSource Source(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var http = new SafeHttpClient(new HttpMessageInvoker(handler), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance);
        return timeout is { } limit ? new CatalogIconSource(http, limit) : new CatalogIconSource(http);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(HttpStatusCode status, byte[]? body = null, string? type = null)
        => (_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
            if (type is not null)
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(response);
        };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RedirectTo(string location)
        => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } });

    [Fact]
    public async Task An_icon_is_downloaded_and_processed()
    {
        var handler = new StubHandler(Answer(HttpStatusCode.OK, IconSamples.Png(1024, 512), "image/png"));

        var icon = (await Source(handler).FetchAsync(Logo, default)).Ok();

        Assert.Equal((256, 128), (icon.Width, icon.Height));
        Assert.Equal([new Uri(Logo)], handler.Requested);
    }

    /// <summary>Design §9: the format comes from the content, never from the name nor the Content-Type.</summary>
    [Fact]
    public async Task A_png_named_jpg_and_served_as_jpeg_is_read_as_a_png()
    {
        var handler = new StubHandler(Answer(HttpStatusCode.OK, IconSamples.Png(64, 64), "image/jpeg"));

        var icon = (await Source(handler).FetchAsync("https://1000logos.net/Google-Logo.jpg", default)).Ok();

        Assert.Equal(IconProcessor.Process(IconSamples.Png(64, 64)).Ok().Hash, icon.Hash);
    }

    [Fact]
    public async Task An_svg_is_refused_whatever_it_is_served_as()
    {
        var handler = new StubHandler(Answer(HttpStatusCode.OK, IconSamples.Svg, "image/png"));
        Assert.Equal(IconCodes.UnsupportedFormat, (await Source(handler).FetchAsync("https://www.nounou-top.fr/logo.svg", default)).FailureCode());
    }

    [Fact]
    public async Task A_host_outside_the_list_is_refused_before_any_request()
    {
        var handler = new StubHandler(Answer(HttpStatusCode.OK, IconSamples.Png(64, 64)));

        Assert.Equal(IconCodes.HostNotAllowed, (await Source(handler).FetchAsync("https://cdn.example.com/netflix.png", default)).FailureCode());
        Assert.Empty(handler.Requested);
    }

    /// <summary>Review Focus 4.</summary>
    [Fact]
    public async Task A_redirect_to_a_host_outside_the_list_is_refused_after_the_first_hop()
    {
        var handler = new StubHandler(RedirectTo("https://cdn.example.com/netflix.png"), Answer(HttpStatusCode.OK, IconSamples.Png(64, 64)));

        Assert.Equal(IconCodes.HostNotAllowed, (await Source(handler).FetchAsync(Logo, default)).FailureCode());
        Assert.Equal([new Uri(Logo)], handler.Requested); // nothing fetched from the CDN
    }

    [Fact]
    public async Task A_redirect_between_allowed_hosts_is_followed()
    {
        var handler = new StubHandler(RedirectTo("https://upload.wikimedia.org/netflix.png"), Answer(HttpStatusCode.OK, IconSamples.Png(64, 64)));

        (await Source(handler).FetchAsync(Logo, default)).Ok();

        Assert.Equal([new Uri(Logo), new Uri("https://upload.wikimedia.org/netflix.png")], handler.Requested);
    }

    [Fact]
    public async Task An_http_url_is_refused()
    {
        var handler = new StubHandler(Answer(HttpStatusCode.OK, IconSamples.Png(64, 64)));
        Assert.Equal(IconCodes.NotHttps, (await Source(handler).FetchAsync("http://1000logos.net/netflix.png", default)).FailureCode());
    }

    [Fact]
    public async Task A_private_address_is_refused()
    {
        var http = new SafeHttpClient(
            new HttpMessageInvoker(OutboundHandler.Create(new FixedResolver("10.0.0.1"))), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance);

        Assert.Equal(IconCodes.AddressRefused, (await new CatalogIconSource(http).FetchAsync(Logo, default)).FailureCode());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task An_error_answer_is_an_http_status(HttpStatusCode status)
    {
        var handler = new StubHandler(Answer(status, "<html>no</html>"u8.ToArray(), "text/html"));
        Assert.Equal(IconCodes.HttpStatus, (await Source(handler).FetchAsync(Logo, default)).FailureCode());
    }

    [Fact]
    public async Task More_than_512_KB_is_too_large()
    {
        var handler = new StubHandler(Answer(HttpStatusCode.OK, new byte[IconLimits.MaxBytes + 1], "image/png"));
        Assert.Equal(IconCodes.TooLarge, (await Source(handler).FetchAsync(Logo, default)).FailureCode());
    }

    [Fact]
    public async Task A_host_that_cannot_be_reached_is_unreachable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        Assert.Equal(IconCodes.Unreachable, (await Source(handler).FetchAsync(Logo, default)).FailureCode());
    }

    [Fact]
    public async Task A_host_that_does_not_answer_in_time_is_unreachable()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.Equal(IconCodes.Unreachable, (await Source(handler, TimeSpan.FromMilliseconds(200)).FetchAsync(Logo, default)).FailureCode());
    }

    [Fact]
    public async Task The_callers_cancellation_is_thrown_not_reported()
    {
        using var shutdown = new CancellationTokenSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            await shutdown.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source(handler).FetchAsync(Logo, shutdown.Token));
    }
}
