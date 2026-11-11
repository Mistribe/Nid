using Nid.Platform;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using UnambitiousFx.Myelin;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Inflation;

namespace Nid.Core.IntegrationTests;

/// <summary>The client behind a stub handler: no network, the default hosts are the allowed ones.</summary>
public class CpiSourceClientTests
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("down");
    }

    private static CpiSourceClient Client(HttpMessageHandler handler, MyelinOptions? outbound = null)
        => new(new SafeHttpClient(new HttpMessageInvoker(handler), outbound ?? NidOutbound.Options(), NullLogger<SafeHttpClient>.Instance),
            new InflationOptions());

    [Fact]
    public async Task It_asks_the_whole_series_of_the_insee_bdm()
    {
        var handler = new StubHandler(HttpStatusCode.OK, 5);

        var result = await Client(handler).FetchAsync(TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var body));
        Assert.Equal(5, body!.Length);
        Assert.Equal("https://bdm.insee.fr/series/sdmx/data/SERIES_BDM/011814630", Assert.Single(handler.Requested).AbsoluteUri);
    }

    [Fact]
    public async Task A_500_is_unavailable_and_a_network_failure_too()
    {
        foreach (HttpMessageHandler handler in new HttpMessageHandler[] { new StubHandler(HttpStatusCode.InternalServerError), new ThrowingHandler() })
        {
            var result = await Client(handler).FetchAsync(TestContext.Current.CancellationToken);
            Assert.True(result.TryGetFailure(out var failure));
            Assert.Equal(InflationCodes.SourceUnavailable, failure!.Code);
        }
    }

    [Fact]
    public async Task An_oversized_body_is_malformed()
    {
        var result = await Client(new StubHandler(HttpStatusCode.OK, bodySize: CpiSourceClient.MaxBytes + 1)).FetchAsync(TestContext.Current.CancellationToken);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(InflationCodes.SourceMalformed, failure!.Code);
    }

    [Fact]
    public async Task A_host_outside_the_list_is_refused_without_a_request()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Client(handler, new MyelinOptions { AllowedHosts = ["www.ecb.europa.eu"] }).FetchAsync(TestContext.Current.CancellationToken);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(InflationCodes.SourceRefused, failure!.Code);
        Assert.Empty(handler.Requested);
    }
}
