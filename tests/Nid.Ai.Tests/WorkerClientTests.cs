using System.Net;
using System.Security.Cryptography;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Workers;
using NodaTime;

namespace Nid.Ai.Tests;

public sealed class WorkerClientTests
{
    [Theory]
    [InlineData(WorkerStatus.Disabled)]
    [InlineData(WorkerStatus.Revoked)]
    [InlineData(WorkerStatus.Pending)]
    public async Task A_worker_that_is_not_active_is_refused_before_anything_is_sent(WorkerStatus status)
    {
        var handler = new CountingHandler();
        var client = new WorkerClient(new HttpClient(handler));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nidKey = new NidKey(Guid.CreateVersion7(), key, "fp", Active: true, Instant.FromUnixTimeSeconds(0));

        var refused = await Assert.ThrowsAsync<WorkerCallException>(() =>
            client.DeclarationAsync(Worker(status), nidKey, TestContext.Current.CancellationToken));

        Assert.Equal(status == WorkerStatus.Revoked ? AiCodes.WorkerRevoked : AiCodes.WorkerNotActive, refused.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Pairing_is_only_sent_to_a_pending_worker()
    {
        var handler = new CountingHandler();
        var client = new WorkerClient(new HttpClient(handler));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nidKey = new NidKey(Guid.CreateVersion7(), key, "fp", Active: true, Instant.FromUnixTimeSeconds(0));

        await Assert.ThrowsAsync<WorkerCallException>(() =>
            client.PairAsync(Worker(WorkerStatus.Active), nidKey, "ABCDEFGH", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    private static WorkerRecord Worker(WorkerStatus status) => new(
        Guid.CreateVersion7(), "GPU box", "worker", 8443, false, WorkerPlacement.Local, false, status, new byte[65], new string('a', 64),
        null, null, null, null, null, null, null, Instant.FromUnixTimeSeconds(0), null, null, 0);

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
