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

    /// <summary>#599, #623: the jobs of <paramref name="fileRef"/> not finished (queued or running) take this data class and these
    /// adults, as the file's owner sees it now; a running one is taken back at its next poll from a worker the new class forbids.
    /// A finished job is left as it is. True when one was. The class is checked as
    /// <see cref="SubmitAsync"/> does (<c>ai.unknown_data_class</c>), <c>none</c> refused: a job never loosens to no data.</summary>
    Task<Result<bool>> ReclassAsync(string fileRef, string dataClass, IReadOnlyList<Guid> concerned, CancellationToken ct);

    /// <summary>#600: the file is gone: its jobs go too, whatever their state, with their input and result (a document's text),
    /// at once rather than at the purge. In the caller's transaction.</summary>
    Task ForgetAsync(string fileRef, CancellationToken ct);

    Task<ModelJobView?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>#625: those of <paramref name="ids"/> still waiting, running or succeeded; a failed, dead or purged one is not.</summary>
    Task<IReadOnlySet<Guid>> LiveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
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

    /// <summary>#602: those of these references the current actor reads, for the jobs page to point at; it names none (an
    /// admin-only answer carries no item's label, <c>docs/identity-and-access/access-tests.md</c> §3.5).</summary>
    Task<IReadOnlySet<string>> ReadableAsync(IReadOnlyCollection<string> references, CancellationToken ct);
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

    /// <summary>Every worker that could take the job is running all it can: it waits for a slot, no attempt counted.</summary>
    public const string WorkerBusy = "ai.worker_busy";
    public const string ResultInvalid = "ai.result_invalid";
    public const string LeaseExpired = "ai.lease_expired";
    public const string TooLarge = "ai.too_large";
    public const string TimedOut = "ai.timed_out";
    public const string FileMissing = "ai.file_missing";

    /// <summary>#167: only a remote worker could take it, the household allows them, but an adult the job concerns has not
    /// chosen <c>allow</c>.</summary>
    public const string OffsiteNotAllowed = "ai.offsite_not_allowed";

    /// <summary>#556: a document type that is not one the household may send to a cloud provider.</summary>
    public const string UnknownKind = "ai.unknown_kind";

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
/// they need to leave the premises (decision M, <c>docs/ai/README.md</c> §6.3). The model is the <c>embed</c> use's.
/// <see cref="Kinds"/> (#556): a document's types (its links' roles); its text reaches a cloud provider only when each is in
/// <c>allowed_kinds</c>, so none (an unfiled document) never does. Null for a search query's text, which only its reader's
/// <c>allow</c> lets out.</summary>
public sealed record LlmEmbedRequest(
    IReadOnlyList<string> Texts, string DataClass, IReadOnlyList<Guid>? Concerned = null, Guid? JobId = null, IReadOnlyList<string>? Kinds = null);

/// <summary>One vector per text, in the texts' order, with the model that made them and their length.</summary>
public sealed record LlmEmbeddings(string Model, int Dimension, IReadOnlyList<IReadOnlyList<float>> Vectors);

/// <summary>How a module embeds text (#558): through the gateway, on the household's workers or, its model being a cloud
/// profile's, on that provider (#556).</summary>
public interface IEmbeddings
{
    /// <summary>The model the <c>embed</c> use runs on now; null while the use is off or has none (#559: what to embed for).</summary>
    Task<string?> ModelAsync(CancellationToken ct);

    /// <summary>#450: whether the household has turned <paramref name="use"/> on (a use such as <c>decide</c> that embeds
    /// through the <c>embed</c> use's model).</summary>
    Task<bool> UseOnAsync(string use, CancellationToken ct) => Task.FromResult(true);

    /// <summary>A refusal carries its <see cref="JobCodes"/> code: the use off or without a model, nowhere to send it, the
    /// worker failed.</summary>
    Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct);
}

/// <summary>#450: short texts (16 at most, 400 characters each), each to put in one of the same <see cref="Options"/> (52 at most, « none of
/// these » included, as the caller names it), with the class of their data and the adults whose <c>allow</c> they need to leave the premises.
/// The model is the <c>decide</c> use's.</summary>
public sealed record LlmDecideRequest(IReadOnlyList<string> Texts, IReadOnlyList<string> Options, string DataClass, IReadOnlyList<Guid>? Concerned = null);

/// <summary>The option a text was put in, by its place in the request's options (null: the model's answer was none of them), and the raw
/// score of each option the model scored, by place (a log-probability; none when the implementation gives none).</summary>
public sealed record LlmDecision(int? Choice, IReadOnlyDictionary<int, double> Scores);

/// <summary>One decision per text, in the texts' order, with what answered: the model, its digest, the implementation and its version
/// (prompt included), and the worker's placement (<c>local</c>, <c>remote</c>).</summary>
public sealed record LlmDecisions(string Model, string Digest, string Implementation, string Version, string Placement, IReadOnlyList<LlmDecision> Decisions);

/// <summary>How the workers implement <c>decide</c> on a model, the one they agree on; <paramref name="Scoreless"/>: it gives no scores.</summary>
public sealed record LlmDecideImplementation(string Name, string Version, bool Scoreless);

/// <summary>#450: how a module asks the <c>decide</c> use's model (<c>docs/ai/README.md</c> §7): on the household's workers only, no cloud
/// adapter serving it yet.</summary>
public interface IDecisions
{
    /// <summary>The model the <c>decide</c> use runs on; null while the use is off or has none.</summary>
    Task<string?> ModelAsync(CancellationToken ct);

    /// <summary>What the active workers declare for <paramref name="model"/> under <c>decide</c>: null when none does or two disagree.</summary>
    Task<LlmDecideImplementation?> ImplementationAsync(string model, CancellationToken ct);

    /// <summary>A refusal carries its <see cref="JobCodes"/> code: the use off or without a model, an input out of bounds, nowhere to send
    /// it, an answer that does not hold.</summary>
    Task<Result<LlmDecisions>> DecideAsync(LlmDecideRequest request, CancellationToken ct);
}

/// <summary>#450: which build of a model answers, for what was measured on it to hold (<c>docs/ai/README.md</c> §7.3).</summary>
public interface IModelDigests
{
    /// <summary>The digest the active workers declare for <paramref name="model"/> under <paramref name="capability"/> (<c>embed</c>,
    /// <c>decide</c>): the one they agree on, null when none declares it or two disagree. The <c>embed</c> use's cloud model is
    /// <c>cloud:&lt;profile id&gt;</c>: its name is versioned by its provider.</summary>
    Task<string?> DigestAsync(string capability, string model, CancellationToken ct);
}
