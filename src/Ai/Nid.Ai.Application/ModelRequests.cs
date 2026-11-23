using System.Runtime.CompilerServices;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

public sealed class ModelRequests(IWorkerRouter router, IWorkerClient client, INidKeys keys, ModelContracts contracts) : IModelRequests
{
    public const int MaxInputBytes = 1024 * 1024;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);
    public static readonly FrameLimits Limits = new(64 * 1024, 10_000, 16L * 1024 * 1024);

    public async IAsyncEnumerable<Result<ReadOnlyMemory<byte>>> StreamAsync(
        string type, int version, byte[] input, string dataClass, Guid? target, [EnumeratorCancellation] CancellationToken ct,
        string? model = null, Action<ServingWorker>? served = null)
    {
        var contract = contracts.Find(ModelCallKind.Request, type, version);
        var refused = contract is null ? JobCodes.UnknownType
            : !DataClasses.IsKnown(dataClass) || (dataClass == DataClasses.None && !contract.AllowsNoData) ? JobCodes.UnknownDataClass
            : input.Length > MaxInputBytes ? JobCodes.TooLarge
            : !contract.ValidInput(input) ? JobCodes.InvalidInput
            : null;
        if (refused is not null)
        {
            yield return ModelFailures.Of(refused);
            yield break;
        }

        var route = await router.RouteAsync(type, version, ModelCallKind.Request, dataClass, target, ct);
        IReadOnlyList<WorkerRecord> workers = model is null ? route.Workers
            : [.. route.Workers.Where(w => WorkerRouter.DeclarationOf(w)?.Models.Any(m => m.Name == model) == true)];
        if (workers.Count == 0)
        {
            yield return ModelFailures.Of(route.Workers.Count == 0 ? route.Reason ?? JobCodes.NoWorker : JobCodes.ModelUnavailable);
            yield break;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxDuration);
        string? failure = null;
        foreach (var worker in workers)
        {
            if (worker.NidKeyId is not { } keyId || await keys.FindAsync(keyId, ct) is not { } key)
                continue;

            await using var frames = client.StreamAsync(worker, key, type, version, input, Limits, timeout.Token).GetAsyncEnumerator(timeout.Token);
            var started = false;
            while (true)
            {
                Frame? frame = null;
                string? cut = null;
                try
                {
                    if (await frames.MoveNextAsync())
                        frame = frames.Current;
                    else
                        cut = JobCodes.WorkerFailed;
                }
                catch (WorkerCallException) when (!started)
                {
                    break; // nothing was answered yet: the next worker
                }
                catch (WorkerCallException)
                {
                    cut = JobCodes.WorkerFailed;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    cut = JobCodes.TimedOut;
                }

                if (frame is null)
                {
                    yield return ModelFailures.Of(cut!);
                    yield break;
                }

                if (!started && frame.Type is FrameType.Data or FrameType.End)
                    served?.Invoke(new ServingWorker(worker.Id, worker.Name, worker.Placement,
                        model is null ? null : WorkerRouter.DeclarationOf(worker)?.Models.FirstOrDefault(m => m.Name == model)?.ContextTokens));
                switch (frame.Type)
                {
                    case FrameType.Data:
                        started = true;
                        if (contract!.ReadOutput(frame.Content.Span) is not { } output)
                        {
                            yield return ModelFailures.Of(JobCodes.ResultInvalid);
                            yield break;
                        }
                        yield return new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(output));
                        continue;
                    case FrameType.End:
                        yield break;
                    default:
                        failure = $"{JobCodes.WorkerFailed}:{System.Text.Encoding.UTF8.GetString(frame.Content.Span)}";
                        if (started)
                        {
                            yield return ModelFailures.Of(failure);
                            yield break;
                        }
                        break; // refused before answering (its engine is down): the next worker may answer
                }

                if (failure is not null)
                    break;
            }
        }

        yield return ModelFailures.Of(failure ?? JobCodes.WorkerFailed);
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
        await foreach (var frame in requests.StreamAsync("echo", 1, ContractJson.Write(new EchoRequestInput("Nid", 3)), "none", worker.Id, ct))
        {
            if (frame.TryGetFailure(out var failed))
                return WorkerFailures.Refused<WorkerTestResult>(failed!.Code);
            frames++;
        }

        var job = await jobs.SubmitAsync(new JobRequest("echo", 1,
            System.Text.Json.JsonSerializer.SerializeToElement(new EchoJobInput($"Nid {Guid.CreateVersion7():N}"), ContractJson.Strict),
            "none", TargetWorkerId: worker.Id), ct);
        return job.TryGet(out var id, out var refused)
            ? Result.Success(new WorkerTestResult(frames, id))
            : WorkerFailures.Refused<WorkerTestResult>(refused!.Code);
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
