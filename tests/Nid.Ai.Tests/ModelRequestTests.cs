using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Tests;

/// <summary>#509: how a streamed request ends when it does not end well. A failure is the stream's last item, with its code.</summary>
public sealed class ModelRequestTests
{
    private static readonly WorkerRecord Salon = new(
        Guid.NewGuid(), "salon", "salon.local", 8443, false, WorkerPlacement.Local, false, WorkerStatus.Active, [], "fp", null, null, null,
        Guid.NewGuid(), null, null, null, Instant.FromUnixTimeSeconds(0), null, null, 0);

    private static readonly Frame Answer = new(FrameType.Data, ContractJson.Write(new EchoRequestOutput("bonjour")));

    [Fact]
    public async Task A_stream_that_stops_before_its_end_frame_is_a_failure_not_a_short_answer()
    {
        var (data, code) = await RunAsync(new ScriptedClient(() => Frames([Answer])));

        Assert.Equal(["""{"text":"bonjour"}"""], data);
        Assert.Equal(JobCodes.WorkerFailed, code);
    }

    [Fact]
    public async Task A_worker_that_breaks_after_answering_fails_the_request()
    {
        var (data, code) = await RunAsync(new ScriptedClient(() => Frames([Answer], new WorkerCallException("ai.worker_unreachable"))));

        Assert.Single(data);
        Assert.Equal(JobCodes.WorkerFailed, code);
    }

    [Fact]
    public async Task A_worker_that_runs_out_of_time_ends_on_timed_out()
    {
        var (data, code) = await RunAsync(new ScriptedClient(() => Frames([Answer], new OperationCanceledException())));

        Assert.Single(data);
        Assert.Equal(JobCodes.TimedOut, code);
    }

    [Fact]
    public async Task An_answer_off_its_contract_is_never_passed_on()
    {
        var (data, code) = await RunAsync(new ScriptedClient(() => Frames([new Frame(FrameType.Data, Encoding.UTF8.GetBytes("""{"words":1}"""))])));

        Assert.Empty(data);
        Assert.Equal(JobCodes.ResultInvalid, code);
    }

    [Fact]
    public async Task An_error_after_answering_carries_the_workers_reason()
    {
        var (data, code) = await RunAsync(new ScriptedClient(() => Frames([Answer, new Frame(FrameType.Error, Encoding.UTF8.GetBytes("engine_down"))])));

        Assert.Single(data);
        Assert.Equal($"{JobCodes.WorkerFailed}:engine_down", code);
    }

    [Fact]
    public async Task A_request_refused_up_front_reaches_no_worker()
    {
        var client = new ScriptedClient(() => Frames([Answer]));

        var unknown = await RunAsync(client, type: "speak");
        var undeclared = await RunAsync(client, model: "llama3.2");

        Assert.Equal(JobCodes.UnknownType, unknown.Code);
        Assert.Equal(JobCodes.ModelUnavailable, undeclared.Code);
        Assert.Empty(unknown.Data.Concat(undeclared.Data));
        Assert.Equal(0, client.Calls);
    }

    /// <summary>The whole stream: its data, and the failure's code, which must be its last item.</summary>
    private static async Task<(List<string> Data, string? Code)> RunAsync(ScriptedClient client, string type = "echo", string? model = null)
    {
        var requests = new ModelRequests(new OneWorker(), client, new Keys(), new ModelContracts([new EchoRequestContract()]));
        var items = new List<Result<ReadOnlyMemory<byte>>>();
        await foreach (var item in requests.StreamAsync(type, 1, ContractJson.Write(new EchoRequestInput("bonjour", 1)), DataClasses.None, null,
                           TestContext.Current.CancellationToken, model: model))
            items.Add(item);

        Assert.All(items.SkipLast(1), i => Assert.True(i.IsSuccess));
        var data = items.Where(i => i.IsSuccess).Select(i => i.TryGetValue(out var d) ? Encoding.UTF8.GetString(d.Span) : "").ToList();
        return (data, items is [.., var last] && last.TryGetFailure(out var failure) ? failure!.Code : null);
    }

    private static async IAsyncEnumerable<Frame> Frames(IEnumerable<Frame> frames, Exception? then = null)
    {
        foreach (var frame in frames)
        {
            await Task.Yield();
            yield return frame;
        }

        if (then is not null)
            throw then;
    }

    private sealed class OneWorker : IWorkerRouter
    {
        public Task<Route> RouteAsync(string type, int version, ModelCallKind kind, string dataClass, Guid? target, CancellationToken ct, bool offsite = true)
            => Task.FromResult(new Route([Salon], null));
    }

    private sealed class Keys : INidKeys
    {
        private readonly NidKey _key = new(Salon.NidKeyId!.Value, ECDsa.Create(ECCurve.NamedCurves.nistP256), "fp", true, Instant.FromUnixTimeSeconds(0));

        public Task<NidKey> ActiveAsync(CancellationToken ct) => Task.FromResult(_key);

        public Task<NidKey?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<NidKey?>(_key);

        public Task<NidKey> RotateAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task RetireUnusedAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ScriptedClient(Func<IAsyncEnumerable<Frame>> stream) : IWorkerClient
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<Frame> StreamAsync(
            WorkerRecord worker, NidKey key, string type, int version, byte[] input, FrameLimits limits, CancellationToken ct)
        {
            Calls++;
            return stream();
        }

        public Task<WorkerKey> GetKeyAsync(Uri url, CancellationToken ct) => throw new NotSupportedException();

        public Task<PairResponse> PairAsync(WorkerRecord worker, NidKey key, string code, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerDeclaration> DeclarationAsync(WorkerRecord worker, NidKey key, CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]> RotateWorkerKeyAsync(WorkerRecord worker, NidKey key, CancellationToken ct) => throw new NotSupportedException();

        public Task SendNidRotationAsync(WorkerRecord worker, NidKey key, NidKey next, CancellationToken ct) => throw new NotSupportedException();

        public Task<SubmittedJob> SubmitJobAsync(WorkerRecord worker, NidKey key, JobSubmission submission, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<PolledJob> PollJobAsync(WorkerRecord worker, NidKey key, string workerJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task CancelJobAsync(WorkerRecord worker, NidKey key, string workerJobId, CancellationToken ct) => throw new NotSupportedException();
    }
}
