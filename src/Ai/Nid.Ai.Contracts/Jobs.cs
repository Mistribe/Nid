using System.Text.Json;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Contracts;

/// <summary>What a job carries: its type and version, its input as its contract reads it, an optional file reference
/// (<c>module:reference</c>, read by that module's <see cref="IJobFileSource"/> when the job is sent), its data class
/// (<c>docs/ai/README.md</c> §6.1) and, for an admin's test, the one worker it must run on.</summary>
public sealed record JobRequest(string Type, int Version, JsonElement Input, string DataClass, string? FileRef = null, Guid? TargetWorkerId = null);

/// <summary>A job's state for the people who look at it: never its input, its file or its secret.</summary>
public sealed record ModelJobView(
    Guid Id, string Type, int Version, string Status, int Attempts, string? WaitingReason, string? Error, Guid? WorkerId,
    Instant CreatedAt, Instant? FinishedAt, string? Result);

/// <summary>How a module sends work to the workers (#509: any module's Application, through these Contracts).</summary>
public interface IModelJobs
{
    /// <summary>Queues a job; the same type and input not finished yet give the same job. Refused with an <c>ai.*</c> code:
    /// an unknown type or data class, an input its contract does not read.</summary>
    Task<Result<Guid>> SubmitAsync(JobRequest request, CancellationToken ct);

    Task<ModelJobView?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>Reads a job's file when it is sent: the module that owns it implements it (Documents for #167).</summary>
public interface IJobFileSource
{
    string Module { get; }

    Task<byte[]?> ReadAsync(string reference, CancellationToken ct);
}
