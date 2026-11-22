extern alias worker;
using System.Net.Http.Json;
using System.Text.Json;
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
        Assert.Equal("job_failed", notice.Kind);
        Assert.Equal("job_failed:ai.test-fail", notice.Key);
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
        return await scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JobView> ViewAsync(PairedWorld world, Guid id)
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
    private static async Task<JobView> RunUntilAsync(PairedWorld world, Guid id, Func<JobView, bool> until, Duration? step = null)
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

        public bool ValidInput(ReadOnlySpan<byte> input) => true;

        public string? ReadOutput(ReadOnlySpan<byte> output) => acceptsOutput ? System.Text.Encoding.UTF8.GetString(output) : null;
    }

    private sealed class Gate : WorkerJobHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => "test-gate";

        public int Version => 1;

        public void Open() => _open.TrySetResult();

        public async Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            await _open.Task.WaitAsync(ct);
            return """{"done": true}"""u8.ToArray();
        }
    }

    private sealed class Failing : WorkerJobHandler
    {
        public string Type => "test-fail";

        public int Version => 1;

        public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct) => throw new InvalidOperationException("fails");
    }

    private sealed class Answering(string type) : WorkerJobHandler
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public string Type => type;

        public int Version => 1;

        public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult("""{"done": true}"""u8.ToArray());
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
}
