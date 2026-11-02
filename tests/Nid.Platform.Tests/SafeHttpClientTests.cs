using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Platform.Outbound;

namespace Nid.Platform.Tests;

public class SafeHttpClientTests
{
    private const string Allowed = "https://raw.githubusercontent.com/file.json";

    /// <summary>Answers each request with the next programmed response and records what was asked.</summary>
    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int next;
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(responses[Math.Min(next++, responses.Length - 1)](request));
        }
    }

    private sealed class FixedResolver(params string[] addresses) : IHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
    }

    private static SafeHttpClient Client(HttpMessageHandler handler)
        => new(new HttpMessageInvoker(handler), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string location)
        => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static async Task<string> RefusalOf(Task task)
        => (await Assert.ThrowsAsync<OutboundRefusedException>(() => task)).Reason;

    [Fact]
    public async Task Returns_the_body_of_an_allowed_host()
    {
        var response = await Client(new StubHandler(_ => Ok("hello"))).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode.OK, "hello"), (response.Status, System.Text.Encoding.UTF8.GetString(response.Body)));
    }

    [Fact]
    public async Task A_404_is_returned_not_thrown()
        => Assert.Equal(HttpStatusCode.NotFound,
            (await Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken)).Status);

    [Theory]
    [InlineData("http://raw.githubusercontent.com/file.json", "not_https")]
    [InlineData("https://evil.example/file.json", "host_not_allowed")]
    [InlineData("https://user:pw@raw.githubusercontent.com/file.json", "user_info")]
    public async Task Refuses_before_sending(string url, string reason)
    {
        var handler = new StubHandler(_ => Ok("never"));
        Assert.Equal(reason, await RefusalOf(Client(handler).GetAsync(new Uri(url), 1024, TestContext.Current.CancellationToken)));
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task Follows_a_redirect_to_an_allowed_host_and_revalidates_each_hop()
    {
        var handler = new StubHandler(_ => Redirect("https://www.ecb.europa.eu/next"), _ => Ok("there"));
        var response = await Client(handler).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken);

        Assert.Equal("there", System.Text.Encoding.UTF8.GetString(response.Body));
        Assert.Equal(["https://raw.githubusercontent.com/file.json", "https://www.ecb.europa.eu/next"], handler.Requested.Select(u => u.ToString()).ToList());
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data", "host_not_allowed")]
    [InlineData("http://raw.githubusercontent.com/downgraded", "not_https")]
    [InlineData("https://evil.example/", "host_not_allowed")]
    public async Task Refuses_a_redirect_that_would_not_be_allowed_directly(string location, string reason)
        => Assert.Equal(reason, await RefusalOf(Client(new StubHandler(_ => Redirect(location))).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken)));

    [Fact]
    public async Task Stops_a_redirect_loop()
        => Assert.Equal("too_many_redirects",
            await RefusalOf(Client(new StubHandler(_ => Redirect(Allowed))).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken)));

    [Fact]
    public async Task Refuses_a_body_over_the_limit_declared_or_streamed()
    {
        Assert.Equal("too_large", await RefusalOf(Client(new StubHandler(_ => Ok(new string('x', 2048)))).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken)));

        // No Content-Length: the limit is enforced while reading.
        var streamed = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[2048])),
        });
        Assert.Equal("too_large", await RefusalOf(Client(streamed).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("8.8.8.8", "10.0.0.1")] // one bad answer is enough
    public async Task The_real_handler_refuses_a_name_resolving_to_a_refused_address(params string[] answers)
    {
        using var handler = OutboundHandler.Create(new FixedResolver(answers));
        Assert.Equal("address_refused", await RefusalOf(Client(handler).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void The_default_hosts_are_the_catalog_the_ecb_the_icon_hosts_the_price_sources_insee_discord_and_enable_banking()
        => Assert.Equal(
            ["raw.githubusercontent.com", "www.ecb.europa.eu", "1000logos.net", "upload.wikimedia.org", "www.nounou-top.fr", "www.boursorama.com", "query2.finance.yahoo.com", "bdm.insee.fr", "discord.com", "discordapp.com", "api.enablebanking.com"],
            new OutboundOptions().AllowedHosts);

    [Fact]
    public async Task Post_sends_the_body_and_refuses_a_redirect()
    {
        var hook = new Uri("https://discord.com/api/webhooks/1/abc");
        string? method = null, contentType = null;
        byte[]? body = null;
        var ok = new StubHandler(r =>
        {
            (method, contentType, body) = (r.Method.Method, r.Content!.Headers.ContentType!.MediaType, r.Content.ReadAsByteArrayAsync().Result);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var response = await Client(ok).PostJsonAsync(hook, [1, 2, 3], 1024, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.NoContent, "POST", "application/json"), (response.Status, method, contentType));
        Assert.Equal(new byte[] { 1, 2, 3 }, body);
        Assert.Equal("redirect_on_post", await RefusalOf(Client(new StubHandler(_ => Redirect("https://discord.com/elsewhere"))).PostJsonAsync(hook, [1], 1024, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SendOnce_sends_the_method_and_headers_to_the_enable_banking_host_and_refuses_its_redirect()
    {
        var api = new Uri("https://api.enablebanking.com/sessions/x");
        string? method = null, auth = null;
        var ok = new StubHandler(r =>
        {
            (method, auth) = (r.Method.Method, r.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var response = await Client(ok).SendOnceAsync(HttpMethod.Delete, api, null, new Dictionary<string, string> { ["Authorization"] = "Bearer t" }, 1024, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.OK, "DELETE", "Bearer t"), (response.Status, method, auth));
        Assert.Equal("redirect_on_get", await RefusalOf(Client(new StubHandler(_ => Redirect("https://discord.com/e"))).SendOnceAsync(HttpMethod.Get, api, null, null, 1024, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Post_refuses_a_host_not_allowed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Equal("host_not_allowed", await RefusalOf(Client(handler).PostJsonAsync(new Uri("https://evil.example/api/webhooks/1/abc"), [1], 1024, TestContext.Current.CancellationToken)));
        Assert.Empty(handler.Requested);
    }

    /// <summary>upload.wikimedia.org answers 403 to a request without one (checked 2026-09-25, 1C2c plan).</summary>
    [Fact]
    public async Task Every_request_names_the_application()
    {
        string? agent = null;
        var handler = new StubHandler(request =>
        {
            agent = request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null;
            return Ok("x");
        });

        await Client(handler).GetAsync(new Uri(Allowed), 1024, TestContext.Current.CancellationToken);

        Assert.Equal("Nid/1.0 (+https://github.com/Mistribe/Nid)", agent);
    }
}
