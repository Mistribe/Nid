using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Ai.Application;

/// <summary>A contract read through <see cref="ContractJson"/>: input <typeparamref name="TIn"/>, output <typeparamref name="TOut"/>.</summary>
public abstract class ModelContract<TIn, TOut>(ModelCallKind kind, string type, int version, bool allowsNoData = false) : IModelContract
    where TIn : IContract
    where TOut : IContract
{
    public ModelCallKind Kind { get; } = kind;

    public string Type { get; } = type;

    public int Version { get; } = version;

    public bool AllowsNoData { get; } = allowsNoData;

    public bool ValidInput(ReadOnlySpan<byte> input) => TryRead<TIn>(input, out _);

    /// <summary>The output as Nid keeps it; null when it does not follow the contract or holds a NUL character, which
    /// Postgres cannot store.</summary>
    public string? ReadOutput(ReadOnlySpan<byte> output)
        => TryRead<TOut>(output, out var read) && Encoding.UTF8.GetString(ContractJson.Write(read!)) is var json && !json.Contains("\\u0000", StringComparison.Ordinal)
            ? json
            : null;

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
public sealed class EchoJobContract() : ModelContract<EchoJobInput, EchoJobOutput>(ModelCallKind.Job, "echo", 1, allowsNoData: true);

/// <summary><c>contracts/requests/echo/v1</c>: the test request « Tester » streams.</summary>
public sealed class EchoRequestContract() : ModelContract<EchoRequestInput, EchoRequestOutput>(ModelCallKind.Request, "echo", 1, allowsNoData: true);

/// <summary><c>contracts/jobs/document-text/v1</c> (#167): a document's text, page by page, from its text layer or OCR.</summary>
public sealed class DocumentTextJobContract() : ModelContract<DocumentTextInput, DocumentTextOutput>(ModelCallKind.Job, "document-text", 1);

/// <summary><c>contracts/requests/chat/v1</c> (#485): a chat turn through a worker's engine; its data class is the caller's.</summary>
public sealed class ChatRequestContract() : ModelContract<ChatRequestInput, ChatRequestOutput>(ModelCallKind.Request, "chat", 1);

/// <summary>The contracts Nid knows, by kind, type and version.</summary>
public sealed class ModelContracts(IEnumerable<IModelContract> contracts)
{
    public IModelContract? Find(ModelCallKind kind, string type, int version)
        => contracts.FirstOrDefault(c => c.Kind == kind && c.Type == type && c.Version == version);
}

public sealed class ModelJobs(IJobStore store, ModelContracts contracts, IClock clock) : IModelJobs
{
    public async Task<Result<Guid>> SubmitAsync(JobRequest request, CancellationToken ct)
    {
        if (contracts.Find(ModelCallKind.Job, request.Type, request.Version) is not { } contract)
            return Refused(JobCodes.UnknownType, "No such job type and version.");
        if (!DataClasses.IsKnown(request.DataClass) || (request.DataClass == DataClasses.None && !contract.AllowsNoData))
            return Refused(JobCodes.UnknownDataClass, "No such data class for this job.");
        var input = JsonSerializer.SerializeToUtf8Bytes(request.Input);
        if (!contract.ValidInput(input))
            return Refused(JobCodes.InvalidInput, "The input does not follow the job's contract.");

        var now = clock.GetCurrentInstant();
        var job = await store.AddOrFindAsync(new JobRecord(
            Guid.CreateVersion7(), request.Type, request.Version, Encoding.UTF8.GetString(input), Hash(request, input), request.FileRef,
            request.DataClass, request.TargetWorkerId, JobState.Queued, 0, now, null, null, null, null, null, null, null, null, now, null,
            null, 0) { Concerned = request.Concerned ?? [] }, ct);
        return Result.Success(job.Id);
    }

    private static Result<Guid> Refused(string code, string message) => Result.Failure<Guid>(new Failure(code, message, null));

    public async Task<ModelJobView?> GetAsync(Guid id, CancellationToken ct)
        => await store.FindAsync(id, ct) is { } job ? View(job) : null;

    public static ModelJobView View(JobRecord job) => new(
        job.Id, job.Type, job.Version, job.Status.ToString().ToLowerInvariant(), job.Attempts, job.WaitingReason, job.Error, job.WorkerId,
        job.CreatedAt, job.FinishedAt, job.Result);

    /// <summary>What makes two submissions the same job: type, version, input, file, data class and target.</summary>
    private static string Hash(JobRequest request, byte[] input)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.Type}\n{request.Version}\n{request.FileRef}\n{request.DataClass}\n{request.TargetWorkerId}\n{Encoding.UTF8.GetString(input)}")));
}

/// <summary>Eligible workers (<c>docs/ai/README.md</c> §4.1): active, seen in the last 3 minutes, declaring the type and
/// version; local first, then the least loaded, then the most recently seen. A target worker is the only route.</summary>
public sealed class WorkerRouter(IWorkerStore workers, IAiSettings settings, IClock clock) : IWorkerRouter
{
    public static readonly Duration OnlineFor = Duration.FromMinutes(3);

    public async Task<Route> RouteAsync(string type, int version, ModelCallKind kind, string dataClass, Guid? target, CancellationToken ct, bool offsite = true)
    {
        var now = clock.GetCurrentInstant();
        var householdAllows = await settings.RemoteWorkersAllowedAsync(ct);
        var remoteAllowed = householdAllows && offsite;
        var refusals = new List<string>();
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
            if (!served.Any(c => c.Type == type && c.Version == version))
                continue;
            if (Domain.DataPolicy.Refusal(worker.Placement, worker.Safe, dataClass, remoteAllowed) is { } refusal)
                refusals.Add(refusal);
            else
                eligible.Add((worker, declaration));
        }

        var ordered = eligible
            .OrderBy(e => e.Worker.Placement == WorkerPlacement.Local ? 0 : 1)
            .ThenBy(e => (double)e.Declaration.Load / Math.Max(1, e.Declaration.MaxConcurrentRequests))
            .ThenByDescending(e => e.Worker.LastSeenAt)
            .Select(e => e.Worker)
            .ToList();
        var reason = Domain.DataPolicy.Reason(refusals);
        if (reason == Domain.DataPolicy.RemoteNotAllowed && householdAllows)
            reason = JobCodes.OffsiteNotAllowed;
        return new Route(ordered, ordered.Count == 0 ? reason ?? JobCodes.NoWorker : null);
    }

    public static WorkerDeclaration? DeclarationOf(WorkerRecord worker) => Declaration(worker);

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
