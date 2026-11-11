using Nid.Platform;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Fx;
using UnambitiousFx.Myelin;
using NodaTime;

namespace Nid.Core.IntegrationTests;

/// <summary>The client behind a stub handler: no network, the default hosts are the allowed ones.</summary>
public class FxSourceClientTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });
    }

    private static FxSourceClient Client(HttpStatusCode status)
        => new(new SafeHttpClient(new HttpMessageInvoker(new StubHandler(status)), NidOutbound.Options(), NullLogger<SafeHttpClient>.Instance),
            new FxOptions());

    [Fact]
    public async Task A_missing_SubTracker_day_is_one_more_day_without_a_file()
    {
        var fetched = await Client(HttpStatusCode.NotFound).SubTrackerDayAsync(new LocalDate(2025, 8, 2), TestContext.Current.CancellationToken);

        Assert.True(fetched.TryGetValue(out var file));
        Assert.Null(file!.Bytes);
    }

    [Fact]
    public async Task A_missing_ECB_history_is_a_source_failure()
    {
        var fetched = await Client(HttpStatusCode.NotFound).EcbHistoryAsync(TestContext.Current.CancellationToken);

        Assert.True(fetched.TryGetFailure(out var failure));
        Assert.Equal(FxCodes.SourceUnavailable, failure!.Code);
    }
}
