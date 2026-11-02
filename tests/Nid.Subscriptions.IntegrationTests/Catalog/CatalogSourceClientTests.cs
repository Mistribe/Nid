using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Platform.Outbound;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>The client behind a stub handler: no network, the default outbound hosts.</summary>
public class CatalogSourceClientTests
{
    private const string Commit = "df0146a8f3532f5c790aa54a34d4871e96b62a95";

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static (CatalogSourceClient Client, StubHandler Handler) Client(Func<Uri, HttpResponseMessage> answer, CatalogOptions? options = null)
    {
        var handler = new StubHandler(answer);
        var http = new SafeHttpClient(new HttpMessageInvoker(handler), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance);
        return (new CatalogSourceClient(http, options ?? new CatalogOptions()), handler);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Both_files_are_read_at_the_pinned_commit()
    {
        var (client, handler) = Client(uri => Ok(uri.AbsolutePath.EndsWith("/labels.json", StringComparison.Ordinal) ? "[\"l\"]" : "[\"p\"]"));

        var files = (await client.FetchAsync(Commit, TestContext.Current.CancellationToken)).Ok();

        Assert.Equal(
            [
                new Uri($"https://raw.githubusercontent.com/Mistribe/SubTracker-Data/{Commit}/labels.json"),
                new Uri($"https://raw.githubusercontent.com/Mistribe/SubTracker-Data/{Commit}/providers.json"),
            ],
            handler.Requested);
        Assert.Equal(("[\"l\"]", "[\"p\"]"), (Encoding.UTF8.GetString(files.Labels), Encoding.UTF8.GetString(files.Providers)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_file_the_source_does_not_serve_is_an_unavailable_source(HttpStatusCode status)
    {
        var (client, _) = Client(_ => new HttpResponseMessage(status));
        Assert.Equal(CatalogCodes.SourceUnavailable, (await client.FetchAsync(Commit, TestContext.Current.CancellationToken)).FailureCode());
    }

    [Fact]
    public async Task A_file_over_one_megabyte_is_malformed()
    {
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[CatalogLimits.MaxFileBytes + 1]) });
        Assert.Equal(CatalogCodes.SourceMalformed, (await client.FetchAsync(Commit, TestContext.Current.CancellationToken)).FailureCode());
    }

    [Fact]
    public async Task A_host_outside_the_allow_list_is_refused_before_any_request()
    {
        var (client, handler) = Client(_ => Ok("[]"), new CatalogOptions { BaseUrl = "https://example.com/catalog" });

        Assert.Equal(CatalogCodes.SourceRefused, (await client.FetchAsync(Commit, TestContext.Current.CancellationToken)).FailureCode());
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task An_unreachable_host_is_an_unavailable_source()
    {
        var (client, _) = Client(_ => throw new HttpRequestException("down"));
        Assert.Equal(CatalogCodes.SourceUnavailable, (await client.FetchAsync(Commit, TestContext.Current.CancellationToken)).FailureCode());
    }

    [Fact]
    public void The_default_commit_is_the_pinned_one()
        => Assert.Equal(CatalogFixtures.PinnedCommit, new CatalogOptions().Commit);
}
