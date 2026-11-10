using System.Text.Json;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Contracts;

/// <summary>core-data-model.md §7 and spec 4C1: the validation queue.</summary>
public enum ProposalStatus { Pending, Approved, Rejected, Superseded }

public sealed record ProposalWarning(string Code, string Severity, string Message, string? Field = null);

/// <summary>
/// What a handler's validation keeps: the business key (deduplication), the warnings and the payload normalized to its
/// known fields — what is fingerprinted, stored and applied, never the client's raw JSON. <paramref name="Conforme"/>
/// (spec 4D1 D3), when set, is <c>{ expected, given }</c>: the document matches what Nid expects and nothing is created.
/// <paramref name="Summary"/> (spec 4D2 §2), when set, is what the assistant's `proposed` and `applied` answers add: the bank
/// table's comparison.
/// </summary>
public sealed record ProposalCheck(
    string? SubjectKey, IReadOnlyList<ProposalWarning> Warnings, JsonElement Payload, JsonElement? Conforme = null, JsonElement? Summary = null);

/// <summary>
/// <paramref name="Target"/> is null for a proposal that creates its item: only its submitter reads and decides it.
/// <paramref name="DocumentId"/> is its source document, checked readable by the host before it is submitted (Core never reads Documents).
/// </summary>
public sealed record NewProposal(
    string Module, string Kind, ItemId? Target, PersonId Person, string? SubjectKey, JsonElement Payload, int PayloadVersion,
    IReadOnlyList<ProposalWarning> Warnings, string CreatedVia, Guid? TokenId, string? SourceLabel, Guid? DocumentId = null);

public sealed record StoredProposal(
    Guid Id, string Module, string Kind, ItemId? Target, PersonId ForPerson, string? SubjectKey, JsonElement Payload,
    int PayloadVersion, JsonElement? EditedPayload, IReadOnlyList<ProposalWarning> Warnings, ProposalStatus Status,
    Guid? SupersededById, string CreatedVia, Guid? TokenId, PersonId CreatedBy, string? SourceLabel, Instant CreatedAt,
    PersonId? DecidedBy, Instant? DecidedAt, string? DecisionNote, IReadOnlyList<Guid> AppliedRefs, uint Version, Guid? DocumentId = null);

public sealed record SubmitOutcome(StoredProposal Proposal, bool Created, IReadOnlyList<Guid>? Superseded = null);

/// <summary>The key of a pending proposal's notice: what publishes it and every place that resolves it.</summary>
public static class ProposalNotices
{
    public static string Key(Guid proposal) => $"proposal_pending:{proposal}";
}

public static class ProposalCodes
{
    public const string KindUnknown = "core.proposal_kind_unknown";
    public const string Conflict = "core.proposal_conflict";
    public const string Decided = "core.proposal_decided";
    public const string VersionUnsupported = "core.proposal_version_unsupported";
    public const string TargetRequired = "core.proposal_target_required";
    public const string NotSubmitter = "core.proposal_not_submitter";

    /// <summary>Warning codes.</summary>
    public const string ApplyFailed = "apply_failed";
    public const string PayloadVersionUnsupported = "payload_version_unsupported";

    /// <summary>4D1: a statement's movement Nid already holds (same reference, or same date, kind and amount).</summary>
    public const string AlreadyRecorded = "already_recorded";
}

/// <summary>
/// A proposal submitted through the pipeline as the ambient actor, as <c>POST /core/proposals</c> submits one: for a module that proposes from
/// its own work (bank sync §4.3) and cannot name Core's command. <c>true</c> when it was stored, <c>false</c> when the same one was pending.
/// </summary>
public interface IProposalSubmitter
{
    Task<Result<bool>> SubmitAsync(string kind, ItemId? item, JsonElement payload, string? sourceLabel, CancellationToken ct);
}

public interface IProposalStore
{
    /// <summary>Deduplicates (same fingerprint pending: returned, not written) and supersedes (same kind, target and subject key pending).</summary>
    Task<SubmitOutcome> SubmitAsync(NewProposal proposal, CancellationToken ct);

    Task<StoredProposal?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>The proposals on <paramref name="targets"/>, and those without a target <paramref name="person"/> submitted, in <paramref name="status"/>, newest first.</summary>
    Task<IReadOnlyList<StoredProposal>> ListAsync(IReadOnlySet<ItemId> targets, PersonId person, ProposalStatus status, CancellationToken ct);

    /// <summary>`pending` → approved or rejected; <see cref="ProposalCodes.Conflict"/> on a stale version, <see cref="ProposalCodes.Decided"/> when not pending.</summary>
    Task<Result<StoredProposal>> DecideAsync(Guid id, uint version, ProposalStatus to, PersonId by, JsonElement? editedPayload,
        string? note, IReadOnlyList<Guid> appliedRefs, CancellationToken ct);

    /// <summary>A `pending` row removed; <see cref="ProposalCodes.Conflict"/> on a stale version, <see cref="ProposalCodes.Decided"/> when not pending.</summary>
    Task<Result<Guid>> DeleteAsync(Guid id, uint version, CancellationToken ct);

    /// <summary>Appends one warning in its own write (after a rolled-back approval), to a row still pending only.</summary>
    Task AddWarningAsync(Guid id, ProposalWarning warning, CancellationToken ct);

    /// <summary>
    /// Core's own, inside the application's transaction: sets <paramref name="documentId"/> on the <c>core.valuation</c> and
    /// <c>core.flow</c> rows among <paramref name="appliedRefs"/>; the other ids (phases, loan rows…) are not Core's facts and are left alone.
    /// </summary>
    Task StampAsync(Guid documentId, IReadOnlyList<Guid> appliedRefs, CancellationToken ct);
}

/// <summary>Breakdowns spec C10: a module stamps the source document on its own rows that an approval wrote.</summary>
public interface IAppliedDocumentStamp
{
    /// <summary>Runs in the approval's transaction; ids that are not this module's rows are ignored.</summary>
    Task StampAsync(Guid documentId, IReadOnlyList<Guid> appliedRefs, CancellationToken ct);
}

/// <summary>
/// One per proposal kind, in its module (spec 4C1 D3). Payloads travel as JSON; the handler parses its own type.
/// <see cref="ApplyAsync"/> runs inside the decision's (or the direct write's) transaction; a command it sends may open its own <c>IUnitOfWork</c>, which joins that transaction under a savepoint.
/// </summary>
public interface IProposalHandler
{
    string Module { get; }
    string Kind { get; }
    int PayloadVersion { get; }

    /// <summary>false for a kind that creates its item (or names its items in the payload): it is then called with no target, as <c>default</c>.</summary>
    bool NeedsTarget => true;

    /// <summary>
    /// Against the current state (<paramref name="payload"/> is a JSON object): a failure refuses (field errors at <c>payload.*</c>), warnings are kept.
    /// <paramref name="edited"/>: the payload is an approval's edit, so the fields only an edit may set (a statement's `excluded`) are kept; a submitter's are dropped.
    /// </summary>
    Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct);

    /// <summary>An approval's edit refused against the submitted payload (a kind whose only edit is a flag keeps its rows); null: allowed.</summary>
    Failure? RefuseEdit(JsonElement submitted, JsonElement edited) => null;

    /// <summary>The current state next to the proposed one, for the decider.</summary>
    Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct);

    /// <summary>Sends the module's command as the current actor; the ids of what it created.</summary>
    Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct);
}
