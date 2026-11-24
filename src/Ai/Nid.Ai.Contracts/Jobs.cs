using System.Text.Json;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Contracts;

/// <summary>What a job carries: its type and version, its input as its contract reads it, an optional file reference
/// (<c>module:reference</c>, read by that module's <see cref="IJobFileSource"/> when the job is sent), its data class
/// (<c>docs/ai/README.md</c> §6.1) and, for an admin's test, the one worker it must run on. <see cref="Concerned"/>: the
/// adults whose <c>allow</c> it needs to leave the premises (decision M, §6.3); without each one's, only a local worker gets it.</summary>
/// <remarks>The input is the contract's JSON (<c>contracts/jobs</c>): a module builds it, the job's contract checks it.</remarks>
public sealed record JobRequest(
    string Type, int Version, JsonElement Input, string DataClass, string? FileRef = null, Guid? TargetWorkerId = null,
    IReadOnlyList<Guid>? Concerned = null);

/// <summary>A job's state for the people who look at it: never its input, its file or its secret.</summary>
public sealed record ModelJobView(
    Guid Id, string Type, int Version, string Status, int Attempts, string? WaitingReason, string? Error, Guid? WorkerId,
    Instant CreatedAt, Instant? FinishedAt, string? Result);

/// <summary>How a module sends work to the workers.</summary>
public interface IModelJobs
{
    /// <summary>Queues a job; the same type and input not finished yet give the same job. A refusal carries its
    /// <see cref="JobCodes"/> code: <c>ai.unknown_type</c>, <c>ai.unknown_data_class</c>, <c>ai.invalid_input</c>.</summary>
    Task<Result<Guid>> SubmitAsync(JobRequest request, CancellationToken ct);

    Task<ModelJobView?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>The module a job type's result is for (#167): told once the job succeeded, before Nid records it so; a sink that
/// throws leaves the job to be run again. Two passes finishing one job at once tell it twice: it keeps the last.</summary>
public interface IJobResultSink
{
    string Type { get; }

    Task AcceptAsync(string? fileRef, string result, CancellationToken ct);
}

/// <summary>Reads a job's file when it is sent: the module that owns it implements it (Documents for #167).</summary>
public interface IJobFileSource
{
    string Module { get; }

    Task<byte[]?> ReadAsync(string reference, CancellationToken ct);

    /// <summary>#602: the jobs page's name for each of these references the current actor may read; the others are left out.</summary>
    Task<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyCollection<string> references, CancellationToken ct);
}

/// <summary>A worker a request may go to: where it runs, whether it is safe and, for the model asked, how much it reads (null
/// when unknown).</summary>
public sealed record ServingWorker(Guid Id, string Name, WorkerPlacement Placement, bool Safe, int? ContextTokens);

/// <summary>How Nid asks a worker for a synchronous answer (<c>docs/ai/README.md</c> §4.1): a chat turn, a search query's
/// embedding, one <c>decide</c>. The answer streams; each piece has been read through the request's contract.</summary>
public interface IModelRequests
{
    /// <summary>The answer's data, piece by piece, then nothing more after the worker's <c>end</c>. A refusal or a failure (no
    /// worker, the worker failed, the stream was cut, a bound passed) is the last item, with its <see cref="JobCodes"/> code.
    /// <paramref name="input"/> is the request contract's JSON (<c>contracts/requests</c>).
    /// <paramref name="model"/>: only the workers that declare it (#486); <paramref name="only"/>: only the workers it accepts (#532);
    /// <paramref name="served"/> learns which worker answered.</summary>
    IAsyncEnumerable<Result<ReadOnlyMemory<byte>>> StreamAsync(
        string type, int version, byte[] input, string dataClass, Guid? target, CancellationToken ct, string? model = null,
        Action<ServingWorker>? served = null, Func<ServingWorker, bool>? only = null);
}

/// <summary>Why a job or request was refused, or a model call failed.</summary>
public static class JobCodes
{
    public const string UnknownDataClass = "ai.unknown_data_class";
    public const string AllOffline = "ai.all_offline";
    public const string SettingsChanged = "ai.settings_changed";
    public const string UnknownType = "ai.unknown_type";
    public const string InvalidInput = "ai.invalid_input";
    public const string NoWorker = "ai.no_worker";
    public const string WorkerFailed = "ai.worker_failed";
    public const string ResultInvalid = "ai.result_invalid";
    public const string LeaseExpired = "ai.lease_expired";
    public const string TooLarge = "ai.too_large";
    public const string TimedOut = "ai.timed_out";
    public const string FileMissing = "ai.file_missing";

    /// <summary>#167: only a remote worker could take it, the household allows them, but an adult the job concerns has not
    /// chosen <c>allow</c>.</summary>
    public const string OffsiteNotAllowed = "ai.offsite_not_allowed";

    /// <summary>#558: no cloud provider serves this use yet (<c>embed</c>, until #556).</summary>
    public const string CloudNotAvailable = "ai.cloud_not_available";

    /// <summary>#486: a use turned off, a use with no model set, a task a use may not run, no worker serving the use's model,
    /// a turn that ended without its usage, a use or mode that does not exist.</summary>
    public const string UseOff = "ai.use_off";
    public const string NoModel = "ai.no_model";
    public const string TaskNotAllowed = "ai.task_not_allowed";
    public const string ModelUnavailable = "ai.model_unavailable";
    public const string Cut = "ai.cut";
    public const string UnknownUse = "ai.unknown_use";
    public const string UnknownMode = "ai.unknown_mode";

    /// <summary>The data policy's refusals (<c>DataPolicy</c>): a remote worker before the household's opt-in, local-only data
    /// with no safe worker.</summary>
    public const string RemoteNotAllowed = "ai.remote_not_allowed";
    public const string NoSafeWorker = "ai.no_safe_worker";

    /// <summary>#487: data a cloud provider may not see, no enabled and eligible profile left, a provider that failed or refused.</summary>
    public const string CloudNotAllowed = "ai.cloud_not_allowed";
    public const string NoCloudProfile = "ai.no_cloud_profile";

    /// <summary>#532: no destination of the conversation's class answers; another class would be a new conversation.</summary>
    public const string DestinationClassUnavailable = "ai.destination_class_unavailable";
    public const string CloudFailed = "ai.cloud_failed";
    public const string CloudRefused = "ai.cloud_refused";

    /// <summary>#488: a paid call over the month's ceiling (or with no rate to price it), and a ceiling out of range.</summary>
    public const string BudgetBlocked = "ai.budget_blocked";
    public const string InvalidBudget = "ai.invalid_budget";
}

/// <summary>A batch of texts to embed (#558): the class of their data and, for background work, the adults whose <c>allow</c>
/// they need to leave the premises (decision M, <c>docs/ai/README.md</c> §6.3). The model is the <c>embed</c> use's.</summary>
public sealed record LlmEmbedRequest(IReadOnlyList<string> Texts, string DataClass, IReadOnlyList<Guid>? Concerned = null, Guid? JobId = null);

/// <summary>One vector per text, in the texts' order, with the model that made them and their length.</summary>
public sealed record LlmEmbeddings(string Model, int Dimension, IReadOnlyList<IReadOnlyList<float>> Vectors);

/// <summary>How a module embeds text (#558): through the gateway, on the household's workers (cloud providers: #556).</summary>
public interface IEmbeddings
{
    /// <summary>The model the <c>embed</c> use runs on now; null while the use is off or has none (#559: what to embed for).</summary>
    Task<string?> ModelAsync(CancellationToken ct);

    /// <summary>A refusal carries its <see cref="JobCodes"/> code: the use off or without a model, nowhere to send it, the
    /// worker failed.</summary>
    Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct);
}
