using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

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

/// <summary><c>contracts/requests/embed/v1</c> (#558): a batch of texts to vectors; its data class is the caller's.</summary>
public sealed class EmbedRequestContract() : ModelContract<EmbedRequestInput, EmbedRequestOutput>(ModelCallKind.Request, "embed", 1);

/// <summary><c>contracts/requests/decide/v1</c> (#450): short texts, each put in one of the same options; its data class is the caller's.</summary>
public sealed class DecideRequestContract() : ModelContract<DecideRequestInput, DecideRequestOutput>(ModelCallKind.Request, "decide", 1);

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
            return ModelFailures.Of(JobCodes.UnknownType);
        if (!DataClasses.IsKnown(request.DataClass) || (request.DataClass == DataClasses.None && !contract.AllowsNoData))
            return ModelFailures.Of(JobCodes.UnknownDataClass);
        var input = JsonSerializer.SerializeToUtf8Bytes(request.Input);
        if (!contract.ValidInput(input))
            return ModelFailures.Of(JobCodes.InvalidInput);

        var now = clock.GetCurrentInstant();
        var job = await store.AddOrFindAsync(new JobRecord(
            Guid.CreateVersion7(), request.Type, request.Version, Encoding.UTF8.GetString(input), Hash(request, input), request.FileRef,
            request.DataClass, request.TargetWorkerId, JobState.Queued, 0, now, null, null, null, null, null, null, null, null, now, null,
            null, 0) { Concerned = request.Concerned ?? [] }, ct);
        return job.Id;
    }

    public async Task<Result<bool>> ReclassAsync(string fileRef, string dataClass, IReadOnlyList<Guid> concerned, CancellationToken ct)
    {
        if (!DataClasses.IsKnown(dataClass) || dataClass == DataClasses.None)
            return ModelFailures.Of(JobCodes.UnknownDataClass);
        return await store.ReclassUnfinishedAsync(fileRef, dataClass, concerned, ct);
    }

    public Task ForgetAsync(string fileRef, CancellationToken ct) => store.DeleteOfFileAsync(fileRef, ct);

    public Task<IReadOnlySet<Guid>> LiveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => store.LiveAsync(ids, ct);

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

/// <summary>Reads the workers and their declarations for <see cref="WorkerRouting"/>, which decides (<c>docs/ai/README.md</c> §4.1).</summary>
public sealed class WorkerRouter(IWorkerStore workers, IAiSettings settings, IClock clock) : IWorkerRouter
{
    public async Task<Route> RouteAsync(string type, int version, ModelCallKind kind, string dataClass, Guid? target, CancellationToken ct, bool offsite = true)
    {
        var all = await workers.ListAsync(ct);
        var candidates = all.Select(worker =>
        {
            var declaration = Declaration(worker);
            var served = declaration is null ? [] : kind == ModelCallKind.Job ? declaration.Jobs : declaration.Requests;
            return new RouteCandidate(worker.Id, worker.Status, worker.Placement, worker.Safe, worker.LastSeenAt,
                served.Any(c => c.Type == type && c.Version == version), declaration?.Load ?? 0, declaration?.MaxConcurrentRequests ?? 1);
        });
        var (ids, reason) = WorkerRouting.Route(
            candidates, clock.GetCurrentInstant(), dataClass, target, await settings.RemoteWorkersAllowedAsync(ct), offsite);
        var byId = all.ToDictionary(w => w.Id);
        return new Route([.. ids.Select(id => byId[id])], reason);
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
