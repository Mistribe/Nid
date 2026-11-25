using System.Text;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Infrastructure.Workers;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Sealing;
using NodaTime;

namespace Nid.Ai.Tests;

/// <summary>#519: the job clock and the router's choice, as pure rules (docs/ai/README.md §4, §4.1).</summary>
public sealed class JobRetriesTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    [InlineData(4, 3600)]
    public void A_failed_attempt_waits_longer_each_time(int attempts, int seconds)
        => Assert.Equal(Duration.FromSeconds(seconds), JobRetries.Backoff(attempts));

    [Fact]
    public void The_fifth_failed_attempt_leaves_the_job_dead()
        => Assert.Null(JobRetries.Backoff(JobRetries.MaxAttempts));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(59, 5)]
    [InlineData(60, 30)]
    [InlineData(600, 30)]
    public void A_job_is_polled_often_in_its_first_minute_then_less(int sentForSeconds, int every)
        => Assert.Equal(Duration.FromSeconds(every), JobRetries.PollEvery(Duration.FromSeconds(sentForSeconds)));

    [Fact]
    public void The_lease_is_renewed_while_the_job_waits_and_while_it_runs_up_to_an_hour_from_its_send()
    {
        var lease = Now + Duration.FromMinutes(3);
        var sent = Now - Duration.FromMinutes(20);
        Assert.Equal(Now + JobRetries.Lease, JobRetries.LeaseAfterPoll(queued: true, Now, sent, lease));
        // #625: a long OCR keeps its lease while its worker says it runs.
        Assert.Equal(Now + JobRetries.Lease, JobRetries.LeaseAfterPoll(queued: false, Now, sent, lease));
        var late = Now - Duration.FromMinutes(55);
        Assert.Equal(late + JobRetries.MaxRun, JobRetries.LeaseAfterPoll(queued: false, Now, late, lease));
        Assert.Equal(lease, JobRetries.LeaseAfterPoll(queued: false, Now, Now - Duration.FromHours(2), lease)); // past the cap: never renewed
    }

    [Theory]
    [InlineData("model_crashed", "model_crashed")]
    [InlineData("out of memory: <script>", "outofmemoryscript")]
    [InlineData("§§§", "unknown")]
    public void A_worker_error_is_kept_as_a_short_code(string error, string code)
        => Assert.Equal(code, JobRetries.ErrorCode(error));

    [Fact]
    public void A_worker_failure_fits_a_code_column_whatever_the_worker_sends()
    {
        Assert.Equal("ai.worker_failed:model_crashed", JobRetries.WorkerFailed("model_crashed"));
        var long_ = JobRetries.WorkerFailed(new string('x', 4096) + " <raw text>");
        Assert.Equal(64, long_.Length);
        Assert.StartsWith("ai.worker_failed:xxx", long_);
    }

    private static RouteCandidate Worker(
        WorkerPlacement placement = WorkerPlacement.Local, bool safe = false, int load = 0, int max = 1, int seenSecondsAgo = 10,
        WorkerStatus status = WorkerStatus.Active, bool serves = true, Guid? id = null)
        => new(id ?? Guid.NewGuid(), status, placement, safe, Now - Duration.FromSeconds(seenSecondsAgo), serves, load, max);

    [Fact]
    public void Local_first_then_the_least_loaded_then_the_most_recently_seen()
    {
        var remote = Worker(WorkerPlacement.Remote);
        var busy = Worker(load: 1, max: 2);
        var idleOld = Worker(seenSecondsAgo: 120);
        var idleRecent = Worker(seenSecondsAgo: 5);

        var (workers, reason) = WorkerRouting.Route([remote, busy, idleOld, idleRecent], Now, DataClasses.Documents, null, householdAllows: true, offsite: true);

        Assert.Equal([idleRecent.Id, idleOld.Id, busy.Id, remote.Id], workers);
        Assert.Null(reason);
    }

    [Fact]
    public void A_worker_not_seen_for_three_minutes_not_active_or_not_serving_is_skipped()
    {
        var (workers, reason) = WorkerRouting.Route(
            [Worker(seenSecondsAgo: 181), Worker(status: WorkerStatus.Disabled), Worker(serves: false)],
            Now, DataClasses.None, null, householdAllows: true, offsite: true);

        Assert.Empty(workers);
        Assert.Equal(JobCodes.NoWorker, reason);
    }

    [Fact]
    public void A_target_worker_is_the_only_route()
    {
        var target = Worker(load: 5, max: 1);

        var (workers, _) = WorkerRouting.Route([Worker(), target], Now, DataClasses.None, target.Id, householdAllows: true, offsite: true);

        Assert.Equal([target.Id], workers);
    }

    [Theory]
    // household opt-in, every concerned adult allows it, reason
    [InlineData(false, true, JobCodes.RemoteNotAllowed)]
    [InlineData(true, false, JobCodes.OffsiteNotAllowed)]
    public void A_remote_worker_kept_out_names_what_kept_it_out(bool householdAllows, bool offsite, string reason)
    {
        var (workers, refused) = WorkerRouting.Route([Worker(WorkerPlacement.Remote)], Now, DataClasses.Documents, null, householdAllows, offsite);

        Assert.Empty(workers);
        Assert.Equal(reason, refused);
    }

    [Fact]
    public void A_job_result_opens_with_the_secret_of_its_submission_only()
    {
        using var worker = WorkerKeyPair.Generate();
        var (_, secret) = SealedRequest.Seal(worker.PublicKey, "{}"u8);
        var (_, other) = SealedRequest.Seal(worker.PublicKey, "{}"u8);
        var output = Encoding.UTF8.GetBytes("""{"total":"12.00"}""");

        Assert.Equal(output, WorkerClient.Open(secret.ToBytes(), Sealed(secret, output, ended: true)));
        Assert.Null(WorkerClient.Open(other.ToBytes(), Sealed(secret, output, ended: true)));
        Assert.Null(WorkerClient.Open(secret.ToBytes(), Sealed(secret, output, ended: false)));
    }

    private static byte[] Sealed(ResponseSecret secret, byte[] output, bool ended)
    {
        var stream = new MemoryStream();
        var writer = new ResponseWriter(secret, stream);
        writer.WriteAsync(FrameType.Data, output, CancellationToken.None).GetAwaiter().GetResult();
        if (ended)
            writer.WriteAsync(FrameType.End, ReadOnlyMemory<byte>.Empty, CancellationToken.None).GetAwaiter().GetResult();
        return stream.ToArray();
    }
}
