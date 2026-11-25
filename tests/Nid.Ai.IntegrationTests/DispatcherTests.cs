extern alias worker;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using WorkerJobHandler = worker::Nid.Ai.Worker.IJobHandler;

namespace Nid.Ai.IntegrationTests;

public sealed class DispatcherTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_echo_job_runs_end_to_end()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();
        var id = await SubmitAsync(world, Echo("hello"));

        var done = await RunUntilAsync(world, id, j => j.Status is "succeeded" or "dead");

        Assert.Equal("succeeded", done.Status);
        Assert.Equal(worker.Id, done.WorkerId);
        Assert.Equal("hello", JsonSerializer.Deserialize<EchoJobOutput>(done.Result!, ContractJson.Strict)!.Text);
        Assert.Null((await StoredAsync(world, id)).ResponseSecret);
    }

    [Fact]
    public async Task A_job_whose_file_cannot_be_sent_fails_with_its_reason_and_reaches_no_worker()
    {
        await using var world = await PairedWorld.StartAsync(fixture, nidServices: s => s.AddSingleton<IJobFileSource>(new Files()));
        await world.PairAsync();
        var gone = await SubmitAsync(world, Echo("gone") with { FileRef = "test:gone" });
        var large = await SubmitAsync(world, Echo("large") with { FileRef = "test:large" });
        var nobody = await SubmitAsync(world, Echo("nobody") with { FileRef = "elsewhere:a" });

        var views = new List<ModelJobView>();
        foreach (var id in new[] { gone, large, nobody })
            views.Add(await RunUntilAsync(world, id, j => j.Status is not ("queued" or "running")));

        Assert.All(views, v => Assert.Equal("failed", v.Status));
        Assert.All(views, v => Assert.Null(v.WorkerId));
        Assert.Equal([JobCodes.FileMissing, JobCodes.TooLarge, JobCodes.FileMissing], views.Select(v => v.Error));
    }

    [Fact]
    public async Task A_job_moves_to_the_other_worker_when_its_worker_disappears()
    {
        var gate = new Gate();
        await using var world = await PairedWorld.StartAsync(fixture, workers: 2,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-gate")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(gate));
        var first = await world.PairAsync(0);
        var second = await world.PairAsync(1);
        var id = await SubmitAsync(world, new JobRequest("test-gate", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));

        await PassAsync(world);
        var sentTo = (await ViewAsync(world, id)).WorkerId;
        Assert.Contains(sentTo, new Guid?[] { first.Id, second.Id });
        world.Routes.Down(new Uri(PairedWorld.UrlOf(sentTo == first.Id ? 0 : 1)).Host);
        gate.Open();

        var done = await RunUntilAsync(world, id, j => j.Status is "succeeded" or "dead");

        Assert.Equal("succeeded", done.Status);
        Assert.NotEqual(sentTo, done.WorkerId);
        Assert.Equal(1, done.Attempts);
        // #602: the retry cleared the job's worker; its dispatches keep both, each with its times and how it ended.
        await using var scope = Scope(world);
        var dispatches = await scope.ServiceProvider.GetRequiredService<IJobStore>().DispatchesOfAsync([id], TestContext.Current.CancellationToken);
        Assert.Equal([sentTo, done.WorkerId], dispatches.Select(d => d.WorkerId));
        Assert.Equal([JobCodes.WorkerFailed, "succeeded"], dispatches.Select(d => d.Outcome));
        Assert.All(dispatches, d => Assert.True(d.FinishedAt >= d.SentAt));
        Assert.True(dispatches[1].SentAt >= dispatches[0].FinishedAt);
    }

    [Fact]
    public async Task Two_workers_share_a_batch_of_jobs()
    {
        // #603: a pass sends a worker no more jobs than it declares it runs at once (4), so a batch of 8 fills both.
        await using var world = await PairedWorld.StartAsync(fixture, workers: 2);
        var first = await world.PairAsync(0);
        var second = await world.PairAsync(1);
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
            ids.Add(await SubmitAsync(world, Echo($"job {i}")));

        var done = new List<ModelJobView>();
        foreach (var id in ids)
            done.Add(await RunUntilAsync(world, id, j => j.Status is "succeeded" or "dead"));

        Assert.All(done, j => Assert.Equal("succeeded", j.Status));
        Assert.Equal(4, done.Count(j => j.WorkerId == first.Id));
        Assert.Equal(4, done.Count(j => j.WorkerId == second.Id));
    }

    [Fact]
    public async Task A_job_whose_lease_runs_out_is_sent_again()
    {
        var gate = new Gate();
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-gate")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(gate));
        await world.PairAsync();
        var id = await SubmitAsync(world, new JobRequest("test-gate", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));
        await PassAsync(world);
        Assert.Equal("running", (await ViewAsync(world, id)).Status);

        world.Clock.Now += Duration.FromMinutes(11);
        await PassAsync(world);

        var view = await ViewAsync(world, id);
        Assert.Equal("queued", view.Status);
        Assert.Equal(1, view.Attempts);
        Assert.Equal(JobCodes.LeaseExpired, view.Error);
        // The copy Nid gave up on is cancelled on the worker: it does not keep one of its slots.
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_keeps_its_lease_while_its_worker_says_it_waits_or_runs()
    {
        var gate = new Gate();
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-gate")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(gate));
        await world.PairAsync();
        // Four run, the fifth waits in the worker's queue.
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await SubmitAsync(world, new JobRequest("test-gate", 1, JsonSerializer.SerializeToElement(new { n = i }), "none")));
        await PassAsync(world);
        for (var minute = 0; minute < 12; minute++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            world.Clock.Now += Duration.FromMinutes(1);
            await PassAsync(world);
        }

        // #625: past the 10-minute lease, the four running (a long OCR) and the one waiting are still on the worker, no attempt
        // counted; the hour from the send that caps them is JobRetriesTests'.
        var views = await Task.WhenAll(ids.Select(id => ViewAsync(world, id)));
        Assert.All(views, v => Assert.Equal(("running", 0, (string?)null), (v.Status, v.Attempts, v.Error)));
    }

    [Fact]
    public async Task A_row_that_cannot_be_stored_does_not_stop_the_other_jobs()
    {
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-nul")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(new Answering("test-nul", """{"text": "a\u0000b"}""")));
        await world.PairAsync();
        var broken = await SubmitAsync(world, new JobRequest("test-nul", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));
        var fine = await SubmitAsync(world, Echo("hello"));

        var done = await RunUntilAsync(world, fine, j => j.Status is "succeeded" or "dead");

        Assert.Equal("succeeded", done.Status);
        var stuck = await ViewAsync(world, broken);
        Assert.Equal(JobCodes.ResultInvalid, stuck.Error);
        Assert.Null(stuck.Result);
    }

    [Fact]
    public async Task A_secret_Nid_can_no_longer_read_sends_the_job_again_without_an_attempt()
    {
        var gate = new Gate();
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-gate")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(gate));
        await world.PairAsync();
        var id = await SubmitAsync(world, new JobRequest("test-gate", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));
        await PassAsync(world);
        var first = (await StoredAsync(world, id)).WorkerJobId;
        await using (var scope = Scope(world))
        {
            var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AiDbContext>();
            await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.ResponseSecret, new byte[] { 1, 2, 3 }), TestContext.Current.CancellationToken);
        }

        world.Clock.Now += Duration.FromSeconds(6);
        await PassAsync(world);

        // Sent again in the same pass, under a new id on the worker; the copy it gave up on is cancelled.
        var again = await StoredAsync(world, id);
        Assert.Equal(JobState.Running, again.Status);
        Assert.NotEqual(first, again.WorkerJobId);
        Assert.Equal(0, again.Attempts);
        Assert.Equal("running", (await ViewAsync(world, id)).Status);
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_result_sealed_for_another_submission_is_refused()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        await world.PairAsync();
        var id = await SubmitAsync(world, Echo("hello"));
        await PassAsync(world);
        using var stranger = Protocol.Sealing.WorkerKeyPair.Generate();
        var other = Protocol.Sealing.SealedRequest.Seal(stranger.PublicKey, "x"u8).Secret.ToBytes();
        await using (var scope = Scope(world))
        {
            var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
            var job = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
            Assert.True(await store.TryUpdateAsync(job with { ResponseSecret = other }, TestContext.Current.CancellationToken));
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);
        world.Clock.Now += Duration.FromSeconds(6);
        await PassAsync(world);

        var view = await ViewAsync(world, id);
        Assert.Equal(JobCodes.ResultInvalid, view.Error);
        Assert.Null(view.Result);
    }

    [Fact]
    public async Task A_job_the_worker_no_longer_knows_is_sent_again()
    {
        var gate = new Gate();
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-gate")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(gate));
        await world.PairAsync();
        var id = await SubmitAsync(world, new JobRequest("test-gate", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));
        await PassAsync(world);
        gate.Open();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        world.Worker.Clock.Now += TimeSpan.FromMinutes(16); // the worker forgot the result

        world.Clock.Now += Duration.FromSeconds(6);
        await PassAsync(world);

        var view = await ViewAsync(world, id);
        Assert.Equal("queued", view.Status);
        Assert.Equal(1, view.Attempts);
    }

    [Fact]
    public async Task A_job_no_worker_can_reach_counts_its_attempts_and_dies()
    {
        var notices = new RecordingNotifier();
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.Replace(ServiceDescriptor.Scoped<INotifier>(_ => notices)));
        await world.PairAsync();
        world.Routes.Down(new Uri(PairedWorld.UrlOf(0)).Host);
        var id = await SubmitAsync(world, Echo("hello"));

        for (var i = 0; i < 6; i++)
        {
            await using (var scope = Scope(world))
            {
                // Seen by the monitor as online (its last declaration is recent), yet nothing answers.
                var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AiDbContext>();
                await db.Workers.ExecuteUpdateAsync(s => s.SetProperty(w => w.LastSeenAt, world.Clock.Now), TestContext.Current.CancellationToken);
            }
            await PassAsync(world);
            world.Clock.Now += Duration.FromHours(2);
        }

        var view = await ViewAsync(world, id);
        Assert.Equal("dead", view.Status);
        Assert.Single(notices.Published);
    }

    [Fact]
    public async Task A_job_refused_for_its_input_fails_at_once()
    {
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-picky")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(new Answering("test-picky")));
        await world.PairAsync();
        // The worker reads the submission strictly: an input that is not an object is refused before the job exists.
        var id = await SubmitAsync(world, new JobRequest("test-picky", 1, JsonSerializer.SerializeToElement(new[] { 1, 2 }), "none"));

        await PassAsync(world);

        var view = await ViewAsync(world, id);
        Assert.Equal("failed", view.Status);
        Assert.Equal(JobCodes.InvalidInput, view.Error);
    }

    [Fact]
    public async Task Five_failures_make_a_job_dead_and_tell_the_admins()
    {
        var notices = new RecordingNotifier();
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s =>
            {
                s.AddSingleton<IModelContract>(new AnyContract("test-fail"));
                s.Replace(ServiceDescriptor.Scoped<INotifier>(_ => notices));
            },
            workerServices: s => s.AddSingleton<WorkerJobHandler>(new Failing()));
        await world.PairAsync();
        var id = await SubmitAsync(world, new JobRequest("test-fail", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));

        var done = await RunUntilAsync(world, id, j => j.Status is "dead" or "succeeded", step: Duration.FromHours(2));

        Assert.Equal("dead", done.Status);
        Assert.Equal(5, done.Attempts);
        var notice = Assert.Single(notices.Published);
        Assert.Equal("platform.job_failed", notice.Kind);
        Assert.Equal("platform.job_failed:ai.test-fail", notice.Key);
    }

    [Fact]
    public async Task A_bad_result_is_not_stored()
    {
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-bad", acceptsOutput: false)),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(new Answering("test-bad")));
        await world.PairAsync();
        var id = await SubmitAsync(world, new JobRequest("test-bad", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));

        await PassAsync(world);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        world.Clock.Now += Duration.FromSeconds(6);
        await PassAsync(world);
        var view = await ViewAsync(world, id);

        Assert.Equal("queued", view.Status);
        Assert.Equal(JobCodes.ResultInvalid, view.Error);
        Assert.Null(view.Result);
    }

    [Fact]
    public async Task Two_passes_at_once_send_a_job_once()
    {
        var counting = new Answering("test-count");
        await using var world = await PairedWorld.StartAsync(fixture,
            nidServices: s => s.AddSingleton<IModelContract>(new AnyContract("test-count")),
            workerServices: s => s.AddSingleton<WorkerJobHandler>(counting));
        await world.PairAsync();
        await SubmitAsync(world, new JobRequest("test-count", 1, JsonSerializer.SerializeToElement(new { n = 1 }), "none"));

        await Task.WhenAll(PassAsync(world), PassAsync(world), PassAsync(world));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, counting.Runs);
    }

    [Fact]
    public async Task Nothing_eligible_leaves_the_job_waiting_with_the_reason()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var id = await SubmitAsync(world, Echo("hello"));

        await PassAsync(world);

        var view = await ViewAsync(world, id);
        Assert.Equal("queued", view.Status);
        Assert.Equal(JobCodes.NoWorker, view.WaitingReason);
        Assert.Equal(0, view.Attempts);
    }

    [Fact]
    public async Task The_monitor_moves_a_worker_to_a_confirmed_nid_key()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();
        var rotation = await (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, "/api/v1/ai/key/rotate"))
            .Content.ReadFromJsonAsync<NidKeyRotation>(PairedWorld.NidJson, TestContext.Current.CancellationToken);
        Assert.Equal(0, world.Worker.Run("confirm-rotation", rotation!.Fingerprint));

        await using (var scope = Scope(world))
            await scope.ServiceProvider.GetRequiredService<IWorkerMonitor>().RunOnceAsync(TestContext.Current.CancellationToken);

        var view = Assert.Single((await world.ListAsync()).Workers);
        Assert.Equal(rotation.Fingerprint.Replace(" ", ""), view.NidKeyFingerprint);
        Assert.NotEqual(worker.NidKeyFingerprint, view.NidKeyFingerprint);
    }

    private static JobRequest Echo(string text) => new("echo", 1, JsonSerializer.SerializeToElement(new EchoJobInput(text), ContractJson.Strict), "none");

    private static AsyncServiceScope Scope(PairedWorld world)
    {
        var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<Guid> SubmitAsync(PairedWorld world, JobRequest request)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(request, TestContext.Current.CancellationToken).OrThrow();
    }

    private static async Task<ModelJobView> ViewAsync(PairedWorld world, Guid id)
    {
        await using var scope = Scope(world);
        return (await scope.ServiceProvider.GetRequiredService<IModelJobs>().GetAsync(id, TestContext.Current.CancellationToken))!;
    }

    private static async Task<JobRecord> StoredAsync(PairedWorld world, Guid id)
    {
        await using var scope = Scope(world);
        return (await scope.ServiceProvider.GetRequiredService<IJobStore>().FindAsync(id, TestContext.Current.CancellationToken))!;
    }

    private static async Task PassAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Monitor and dispatcher passes, as the scheduler runs them, the clock moving <paramref name="step"/> (31 s by
    /// default) between them, until the job reaches what the test waits for.</summary>
    private static async Task<ModelJobView> RunUntilAsync(PairedWorld world, Guid id, Func<ModelJobView, bool> until, Duration? step = null)
    {
        for (var i = 0; i < 80; i++)
        {
            await using (var scope = Scope(world))
                await scope.ServiceProvider.GetRequiredService<IWorkerMonitor>().RunOnceAsync(TestContext.Current.CancellationToken);
            await PassAsync(world);
            var view = await ViewAsync(world, id);
            if (until(view))
                return view;
            await Task.Delay(20, TestContext.Current.CancellationToken);
            world.Clock.Now += step ?? Duration.FromSeconds(31);
        }

        throw new TimeoutException($"Job {id} stayed {(await ViewAsync(world, id)).Status}.");
    }

    /// <summary>Any JSON object in and out (for a test type), or no output accepted at all.</summary>
    private sealed class AnyContract(string type, bool acceptsOutput = true) : IModelContract
    {
        public ModelCallKind Kind => ModelCallKind.Job;

        public string Type => type;

        public int Version => 1;

        public bool AllowsNoData => true;

        public bool ValidInput(ReadOnlySpan<byte> input) => true;

        public string? ReadOutput(ReadOnlySpan<byte> output) => acceptsOutput ? System.Text.Encoding.UTF8.GetString(output) : null;
    }

    private sealed class Gate : WorkerJobHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => "test-gate";

        public int Version => 1;

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public async Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            try
            {
                await _open.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return """{"done": true}"""u8.ToArray();
        }
    }

    private sealed class Failing : WorkerJobHandler
    {
        public string Type => "test-fail";

        public int Version => 1;

        public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct) => throw new InvalidOperationException("fails");
    }

    private sealed class Answering(string type, string output = """{"done": true}""") : WorkerJobHandler
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Type => type;

        public int Version => 1;

        public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(output));
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        private readonly List<Notice> _published = [];

        public IReadOnlyList<Notice> Published
        {
            get
            {
                lock (_published)
                    return [.. _published];
            }
        }

        public Task PublishAsync(Notice notice, CancellationToken ct)
        {
            lock (_published)
                _published.Add(notice);
            return Task.CompletedTask;
        }

        public Task ResolveAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The files of the module <c>test</c>: <c>large</c> is over what a job may carry, any other is missing.</summary>
    private sealed class Files : IJobFileSource
    {
        public string Module => "test";

        public Task<byte[]?> ReadAsync(string reference, CancellationToken ct)
            => Task.FromResult(reference == "large" ? new byte[JobSubmission.MaxFileBytes + 1] : null);

        public Task<IReadOnlySet<string>> ReadableAsync(IReadOnlyCollection<string> references, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }
}
