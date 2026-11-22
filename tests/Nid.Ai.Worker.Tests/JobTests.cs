using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Sealing;
using Nid.Ai.Worker;

namespace Nid.Ai.Worker.Tests;

public sealed class JobTests
{
    [Fact]
    public async Task An_echo_job_runs_and_its_result_opens_only_with_the_submission_secret()
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);

        var (id, error, submission) = await nid.SubmitAsync("echo", 1, new EchoJobInput("hello"));
        Assert.Null(error);
        var (status, result) = await nid.WaitAsync(id!);

        Assert.Equal("succeeded", status.Status);
        Assert.Equal("hello", ContractJson.Read<EchoJobOutput>(await TestNid.OpenResultAsync(result!, submission)).Text);

        using var stranger = WorkerKeyPair.Generate();
        var other = SealedRequest.Seal(stranger.PublicKey, "x"u8).Secret;
        await Assert.ThrowsAsync<ProtocolException>(() => TestNid.OpenResultAsync(result!, other));
    }

    [Fact]
    public async Task A_large_result_comes_back_whole_across_frames()
    {
        await using var worker = new WorkerHarness(services: s => s.AddSingleton<IJobHandler>(new BigJob()));
        using var nid = await PairedAsync(worker);

        var (id, _, submission) = await nid.SubmitAsync(BigJob.Name, 1, new { n = 1 });
        var (status, result) = await nid.WaitAsync(id!);

        Assert.Equal("succeeded", status.Status);
        Assert.True(result!.Length > 3 * JobStatus.ResultChunk);
        Assert.Equal(BigJob.Output, await TestNid.OpenResultAsync(result, submission));
    }

    [Theory]
    [InlineData("speak", 1)]
    [InlineData("echo", 2)]
    public async Task An_unknown_type_or_version_is_refused(string type, int version)
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);

        var (id, error, _) = await nid.SubmitAsync(type, version, new EchoJobInput("hello"));

        Assert.Null(id);
        Assert.Equal("unsupported", error);
    }

    [Fact]
    public async Task A_bad_input_fails_the_job()
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);

        var (id, _, _) = await nid.SubmitAsync("echo", 1, new { words = "hello" });

        var (status, _) = await nid.WaitAsync(id!);
        Assert.Equal("failed", status.Status);
        Assert.Equal("input_invalid", status.Error);
    }

    [Fact]
    public async Task A_full_queue_refuses_the_next_job()
    {
        var gate = new BlockingJob();
        await using var worker = new WorkerHarness(services: s => s.AddSingleton<IJobHandler>(gate));
        using var nid = await PairedAsync(worker);

        for (var i = 0; i < JobTable.MaxJobs; i++)
            Assert.NotNull((await nid.SubmitAsync(BlockingJob.Name, 1, new { n = i })).Id);

        Assert.Equal("busy", (await nid.SubmitAsync(BlockingJob.Name, 1, new { n = -1 })).Error);
        gate.Release();
    }

    [Fact]
    public async Task A_finished_job_is_forgotten_after_fifteen_minutes()
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);
        var (id, _, _) = await nid.SubmitAsync("echo", 1, new EchoJobInput("hello"));
        await nid.WaitAsync(id!);

        worker.Clock.Now += TimeSpan.FromMinutes(16);

        Assert.Equal("unknown", (await nid.PollAsync(id!)).Status.Status);
    }

    [Fact]
    public async Task A_cancelled_job_fails_as_cancelled()
    {
        var gate = new BlockingJob();
        await using var worker = new WorkerHarness(services: s => s.AddSingleton<IJobHandler>(gate));
        using var nid = await PairedAsync(worker);
        var (id, _, _) = await nid.SubmitAsync(BlockingJob.Name, 1, new { n = 1 });

        var (status, _, _) = await nid.CallFramesAsync($"/v1/jobs/{id}/cancel", "{}"u8.ToArray(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);
        var (after, _) = await nid.WaitAsync(id!);
        Assert.Equal("failed", after.Status);
        Assert.Equal("cancelled", after.Error);
    }

    [Fact]
    public async Task The_declaration_lists_echo_and_the_load()
    {
        var gate = new BlockingJob();
        await using var worker = new WorkerHarness(services: s => s.AddSingleton<IJobHandler>(gate));
        using var nid = await PairedAsync(worker);
        await nid.SubmitAsync(BlockingJob.Name, 1, new { n = 1 });
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var declaration = (await nid.DeclarationAsync()).Body!;

        Assert.Contains(declaration.Jobs, j => j is { Type: "echo", Version: 1 });
        Assert.Contains(declaration.Requests, r => r is { Type: "echo", Version: 1 });
        Assert.Equal(1, declaration.Load);
        gate.Release();
    }

    [Fact]
    public async Task A_streamed_echo_returns_each_frame_then_ends()
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);

        var (status, frames, _) = await nid.CallFramesAsync("/v1/requests/echo/1", ContractJson.Write(new EchoRequestInput("hi", 3)), ct: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([FrameType.Data, FrameType.Data, FrameType.Data, FrameType.End], frames.Select(f => f.Type));
        Assert.All(frames.Take(3), f => Assert.Equal("hi", ContractJson.Read<EchoRequestOutput>(f.Content.Span).Text));
    }

    [Fact]
    public async Task An_unknown_request_type_ends_in_an_error_frame()
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);

        var (_, frames, _) = await nid.CallFramesAsync("/v1/requests/speak/1", "{}"u8.ToArray(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FrameType.Error, Assert.Single(frames).Type);
        Assert.Equal("unsupported", Encoding.UTF8.GetString(frames[0].Content.Span));
    }

    [Fact]
    public async Task Closing_a_request_stops_its_handler()
    {
        var endless = new EndlessRequest();
        await using var worker = new WorkerHarness(services: s => s.AddSingleton<IRequestHandler>(endless));
        using var nid = await PairedAsync(worker);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            nid.CallFramesAsync($"/v1/requests/{EndlessRequest.Name}/1", "{}"u8.ToArray(), ct: cts.Token));

        Assert.True(await endless.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private static async Task<TestNid> PairedAsync(WorkerHarness worker)
    {
        var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        await nid.PairAsync(worker.PairingCode());
        return nid;
    }

    private sealed class BigJob : IJobHandler
    {
        public const string Name = "test-big";
        public static readonly byte[] Output = Encoding.UTF8.GetBytes($$"""{"text": "{{new string('x', 200_000)}}"}""");

        public string Type => Name;

        public int Version => 1;

        public Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct) => Task.FromResult(Output);
    }

    private sealed class BlockingJob : IJobHandler
    {
        public const string Name = "test-block";
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => Name;

        public int Version => 1;

        public void Release() => _open.TrySetResult();

        public async Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            await _open.Task.WaitAsync(ct);
            return "{}"u8.ToArray();
        }
    }

    private sealed class EndlessRequest : IRequestHandler
    {
        public const string Name = "test-endless";

        public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => Name;

        public int Version => 1;

        public async IAsyncEnumerable<byte[]> RunAsync(byte[] input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(20, ct);
                    yield return "{}"u8.ToArray();
                }
            }
            finally
            {
                Stopped.TrySetResult(true);
            }
        }
    }
}
