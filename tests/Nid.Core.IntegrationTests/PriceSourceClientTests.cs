using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Investments.Contracts;
using Nid.Investments.Infrastructure.Prices;
using Nid.Platform.Outbound;

namespace Nid.Core.IntegrationTests;

/// <summary>The client behind a stub handler: no network, the default hosts are the allowed ones.</summary>
public class PriceSourceClientTests
{
    private sealed class StubHandler(HttpStatusCode status, int bodySize = 0) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[bodySize]) });
        }
    }

    private static PriceSourceClient Client(HttpMessageHandler handler, PriceOptions? options = null, OutboundOptions? outbound = null)
        => new(new SafeHttpClient(new HttpMessageInvoker(handler), outbound ?? new OutboundOptions(), NullLogger<SafeHttpClient>.Instance),
            options ?? new PriceOptions());

    [Fact]
    public async Task Each_method_asks_its_url_with_the_search_query_escaped()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = Client(handler);

        await client.BoursoramaSearchAsync("FR0010148981", default);
        await client.BoursoramaPageAsync("/bourse/trackers/cours/1rTCW8/", default);
        await client.YahooSearchAsync("FR 0&1", default);
        await client.YahooChartAsync("CW8.PA", default);

        Assert.Equal(
            [
                "https://www.boursorama.com/recherche/ajax?query=FR0010148981",
                "https://www.boursorama.com/bourse/trackers/cours/1rTCW8/",
                "https://query2.finance.yahoo.com/v1/finance/search?q=FR%200%261&quotesCount=3&newsCount=0",
                "https://query2.finance.yahoo.com/v8/finance/chart/CW8.PA?range=5d&interval=1d",
            ],
            handler.Requested.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task A_404_is_an_empty_fetch_and_a_500_is_unavailable()
    {
        var missing = await Client(new StubHandler(HttpStatusCode.NotFound)).YahooChartAsync("X", default);
        Assert.True(missing.TryGetValue(out var fetch));
        Assert.Null(fetch!.Body);

        var broken = await Client(new StubHandler(HttpStatusCode.InternalServerError)).BoursoramaPageAsync("/p/", default);
        Assert.True(broken.TryGetFailure(out var failure));
        Assert.Equal(PriceCodes.SourceUnavailable, failure!.Code);
    }

    [Fact]
    public async Task An_oversized_body_is_malformed()
    {
        var result = await Client(new StubHandler(HttpStatusCode.OK, bodySize: 3 * 1024 * 1024 + 1)).YahooChartAsync("X", default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PriceCodes.SourceMalformed, failure!.Code);
    }

    [Fact]
    public async Task A_host_outside_the_list_is_refused_without_a_request()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Client(handler, outbound: new OutboundOptions { AllowedHosts = ["www.ecb.europa.eu"] }).BoursoramaSearchAsync("FR0010148981", default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PriceCodes.SourceRefused, failure!.Code);
        Assert.Empty(handler.Requested);
    }

    [Theory]
    [InlineData("//query2.finance.yahoo.com/x")]
    [InlineData("https://raw.githubusercontent.com/x")]
    [InlineData("x/y")]
    [InlineData("http://")]
    [InlineData("//")]
    [InlineData("")]
    public async Task A_page_path_that_leaves_the_host_is_malformed_without_a_request(string path)
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Client(handler).BoursoramaPageAsync(path, default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PriceCodes.SourceMalformed, failure!.Code);
        Assert.Empty(handler.Requested);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task A_symbol_of_only_dots_is_malformed_without_a_request(string symbol)
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Client(handler).YahooChartAsync(symbol, default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PriceCodes.SourceMalformed, failure!.Code);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task A_network_failure_is_unavailable_never_a_throw()
    {
        var result = await Client(new ThrowingHandler()).YahooSearchAsync("FR0010148981", default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PriceCodes.SourceUnavailable, failure!.Code);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("down");
    }
}
