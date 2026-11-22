using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using NodaTime;

namespace Nid.Ai.Application;

/// <summary>A contract read through <see cref="ContractJson"/>: input <typeparamref name="TIn"/>, output <typeparamref name="TOut"/>.</summary>
public abstract class ModelContract<TIn, TOut>(ModelCallKind kind, string type, int version) : IModelContract
    where TIn : IContract
    where TOut : IContract
{
    public ModelCallKind Kind { get; } = kind;

    public string Type { get; } = type;

    public int Version { get; } = version;

    public bool ValidInput(ReadOnlySpan<byte> input) => TryRead<TIn>(input, out _);

    public string? ReadOutput(ReadOnlySpan<byte> output)
        => TryRead<TOut>(output, out var read) ? Encoding.UTF8.GetString(ContractJson.Write(read!)) : null;

    private static bool TryRead<T>(ReadOnlySpan<byte> json, out T? value) where T : IContract
    {
        try
        {
            value = ContractJson.Read<T>(json);
            return true;
        }
        catch (ProtocolException)
        {
            value = default;
            return false;
        }
    }
}

/// <summary><c>contracts/jobs/echo/v1</c>: the test job « Tester » sends.</summary>
public sealed class EchoJobContract() : ModelContract<EchoJobInput, EchoJobOutput>(ModelCallKind.Job, "echo", 1);

/// <summary><c>contracts/requests/echo/v1</c>: the test request « Tester » streams.</summary>
public sealed class EchoRequestContract() : ModelContract<EchoRequestInput, EchoRequestOutput>(ModelCallKind.Request, "echo", 1);

/// <summary>The contracts Nid knows, by kind, type and version.</summary>
public sealed class ModelContracts(IEnumerable<IModelContract> contracts)
{
    public IModelContract? Find(ModelCallKind kind, string type, int version)
        => contracts.FirstOrDefault(c => c.Kind == kind && c.Type == type && c.Version == version);
}

public sealed class ModelJobs(IJobStore store, ModelContracts contracts, IClock clock) : IModelJobs
{
    public async Task<Guid> SubmitAsync(JobRequest request, CancellationToken ct)
    {
        var contract = contracts.Find(ModelCallKind.Job, request.Type, request.Version)
                       ?? throw new ModelCallException(JobCodes.UnknownType);
        var input = JsonSerializer.SerializeToUtf8Bytes(request.Input);
        if (!contract.ValidInput(input))
            throw new ModelCallException(JobCodes.InvalidInput);

        var now = clock.GetCurrentInstant();
        var job = await store.AddOrFindAsync(new JobRecord(
            Guid.CreateVersion7(), request.Type, request.Version, Encoding.UTF8.GetString(input), Hash(request, input), request.FileRef,
            request.DataClass, request.TargetWorkerId, JobState.Queued, 0, now, null, null, null, null, null, null, null, null, now, null,
            null, 0), ct);
        return job.Id;
    }

    public async Task<JobView?> GetAsync(Guid id, CancellationToken ct)
        => await store.FindAsync(id, ct) is { } job ? View(job) : null;

    public static JobView View(JobRecord job) => new(
        job.Id, job.Type, job.Version, job.Status.ToString().ToLowerInvariant(), job.Attempts, job.WaitingReason, job.Error, job.WorkerId,
        job.CreatedAt, job.FinishedAt, job.Result);

    /// <summary>What makes two submissions the same job: type, version, input, file, data class and target.</summary>
    private static string Hash(JobRequest request, byte[] input)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.Type}\n{request.Version}\n{request.FileRef}\n{request.DataClass}\n{request.TargetWorkerId}\n{Encoding.UTF8.GetString(input)}")));
}

/// <summary>Eligible workers (<c>docs/ai/README.md</c> §4.1): active, seen in the last 3 minutes, declaring the type and
/// version; local first, then the least loaded, then the most recently seen. A target worker is the only route.</summary>
public sealed class WorkerRouter(IWorkerStore workers, IClock clock) : IWorkerRouter
{
    public static readonly Duration OnlineFor = Duration.FromMinutes(3);

    public async Task<Route> RouteAsync(string type, int version, ModelCallKind kind, string dataClass, Guid? target, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var eligible = new List<(WorkerRecord Worker, WorkerDeclaration Declaration)>();
        foreach (var worker in await workers.ListAsync(ct))
        {
            if (worker.Status != WorkerStatus.Active || worker.LastSeenAt is not { } seen || now - seen > OnlineFor)
                continue;
            if (target is { } only && worker.Id != only)
                continue;
            if (Declaration(worker) is not { } declaration)
                continue;
            var served = kind == ModelCallKind.Job ? declaration.Jobs : declaration.Requests;
            if (served.Any(c => c.Type == type && c.Version == version))
                eligible.Add((worker, declaration));
        }

        var ordered = eligible
            .OrderBy(e => e.Worker.Placement == WorkerPlacement.Local ? 0 : 1)
            .ThenBy(e => (double)e.Declaration.Load / Math.Max(1, e.Declaration.MaxConcurrentRequests))
            .ThenByDescending(e => e.Worker.LastSeenAt)
            .Select(e => e.Worker)
            .ToList();
        return new Route(ordered, ordered.Count == 0 ? JobCodes.NoWorker : null);
    }

    private static WorkerDeclaration? Declaration(WorkerRecord worker)
    {
        if (worker.LastDeclaration is null)
            return null;
        try
        {
            return ContractJson.Read<WorkerDeclaration>(Encoding.UTF8.GetBytes(worker.LastDeclaration));
        }
        catch (ProtocolException)
        {
            return null;
        }
    }
}
