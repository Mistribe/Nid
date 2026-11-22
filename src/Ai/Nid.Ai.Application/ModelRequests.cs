using System.Runtime.CompilerServices;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>How Nid asks a worker for a synchronous answer (<c>docs/ai/README.md</c> §4.1): a chat turn, a search query's
/// embedding, one <c>decide</c>. The answer streams; each piece has been read through the request's contract.</summary>
public interface IModelRequests
{
    /// <summary>The answer's data, piece by piece. Ends normally only after the worker's <c>end</c>; throws
    /// <see cref="ModelCallException"/> otherwise (no worker, the worker failed, the stream was cut, a bound passed).</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(string type, int version, byte[] input, string dataClass, Guid? target, CancellationToken ct);
}

public sealed class ModelRequests(IWorkerRouter router, IWorkerClient client, INidKeys keys, ModelContracts contracts) : IModelRequests
{
    public const int MaxInputBytes = 1024 * 1024;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);
    public static readonly FrameLimits Limits = new(64 * 1024, 10_000, 16L * 1024 * 1024);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(
        string type, int version, byte[] input, string dataClass, Guid? target, [EnumeratorCancellation] CancellationToken ct)
    {
        var contract = contracts.Find(ModelCallKind.Request, type, version) ?? throw new ModelCallException(JobCodes.UnknownType);
        if (!DataClasses.IsKnown(dataClass))
            throw new ModelCallException(JobCodes.UnknownDataClass);
        if (input.Length > MaxInputBytes)
            throw new ModelCallException(JobCodes.TooLarge);
        if (!contract.ValidInput(input))
            throw new ModelCallException(JobCodes.InvalidInput);

        var route = await router.RouteAsync(type, version, ModelCallKind.Request, dataClass, target, ct);
        if (route.Workers.Count == 0)
            throw new ModelCallException(route.Reason ?? JobCodes.NoWorker);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxDuration);
        foreach (var worker in route.Workers)
        {
            if (worker.NidKeyId is not { } keyId || await keys.FindAsync(keyId, ct) is not { } key)
                continue;

            await using var frames = client.StreamAsync(worker, key, type, version, input, Limits, timeout.Token).GetAsyncEnumerator(timeout.Token);
            var started = false;
            while (true)
            {
                Frame frame;
                try
                {
                    if (!await frames.MoveNextAsync())
                        throw new ModelCallException(JobCodes.WorkerFailed);
                    frame = frames.Current;
                }
                catch (WorkerCallException) when (!started)
                {
                    break; // nothing was answered yet: the next worker
                }
                catch (WorkerCallException)
                {
                    throw new ModelCallException(JobCodes.WorkerFailed);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ModelCallException(JobCodes.TimedOut);
                }

                switch (frame.Type)
                {
                    case FrameType.Data:
                        started = true;
                        yield return System.Text.Encoding.UTF8.GetBytes(contract.ReadOutput(frame.Content.Span)
                                                                       ?? throw new ModelCallException(JobCodes.ResultInvalid));
                        continue;
                    case FrameType.End:
                        yield break;
                    default:
                        throw new ModelCallException($"{JobCodes.WorkerFailed}:{System.Text.Encoding.UTF8.GetString(frame.Content.Span)}");
                }
            }
        }

        throw new ModelCallException(JobCodes.WorkerFailed);
    }
}

/// <summary>« Tester » on a worker: a streamed <c>echo</c> through it, and an <c>echo</c> job queued for it.</summary>
public sealed record TestWorkerCommand(Guid Id) : ICommand<WorkerTestResult>, IWorkerAdminCommand
{
    public SecurityEvent? EventFor(object response) => null;
}

/// <summary>A job's state, for an admin (its result may carry what the household sent).</summary>
public sealed record GetModelJobQuery(Guid Id) : IQuery<ModelJobView>, IAdminOnly;

[RequestHandler<TestWorkerCommand, WorkerTestResult>]
public sealed class TestWorkerHandler(IWorkerStore workers, IModelRequests requests, IModelJobs jobs)
    : IRequestHandler<TestWorkerCommand, WorkerTestResult>
{
    public async ValueTask<Result<WorkerTestResult>> HandleAsync(TestWorkerCommand command, CancellationToken ct = default)
    {
        if (await workers.FindAsync(command.Id, ct) is not { } worker)
            return WorkerFailures.Refused<WorkerTestResult>(AiCodes.WorkerNotFound);
        if (Domain.WorkerRules.Call(worker.Status) is { } refusal)
            return WorkerFailures.Refused<WorkerTestResult>(refusal);

        var frames = 0;
        try
        {
            await foreach (var _ in requests.StreamAsync("echo", 1, ContractJson.Write(new EchoRequestInput("Nid", 3)), "none", worker.Id, ct))
                frames++;
        }
        catch (ModelCallException e)
        {
            return WorkerFailures.Refused<WorkerTestResult>(e.Code);
        }

        var job = await jobs.SubmitAsync(new JobRequest("echo", 1,
            System.Text.Json.JsonSerializer.SerializeToElement(new EchoJobInput($"Nid {Guid.CreateVersion7():N}"), ContractJson.Strict),
            "none", TargetWorkerId: worker.Id), ct);
        return Result.Success(new WorkerTestResult(frames, job));
    }
}

[RequestHandler<GetModelJobQuery, ModelJobView>]
public sealed class GetModelJobHandler(IModelJobs jobs) : IRequestHandler<GetModelJobQuery, ModelJobView>
{
    public async ValueTask<Result<ModelJobView>> HandleAsync(GetModelJobQuery query, CancellationToken ct = default)
        => await jobs.GetAsync(query.Id, ct) is { } view
            ? Result.Success(view)
            : Result.Failure<ModelJobView>(new UnambitiousFx.Functional.Failures.NotFoundFailure("job", query.Id.ToString()));
}
