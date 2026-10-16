using System.Text.Json;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>core-data-model.md §7 and spec 4C1: the validation queue.</summary>
public enum ProposalStatus { Pending, Approved, Rejected, Superseded }

public sealed record ProposalWarning(string Code, string Severity, string Message, string? Field = null);

/// <summary>What a handler's validation keeps: the business key (deduplication) and the warnings.</summary>
public sealed record ProposalCheck(string? SubjectKey, IReadOnlyList<ProposalWarning> Warnings);

public sealed record NewProposal(
    string Module, string Kind, ItemId Target, PersonId Person, string? SubjectKey, JsonElement Payload, int PayloadVersion,
    IReadOnlyList<ProposalWarning> Warnings, string CreatedVia, Guid? TokenId, string? SourceLabel);

public sealed record StoredProposal(
    Guid Id, string Module, string Kind, ItemId Target, PersonId ForPerson, string? SubjectKey, JsonElement Payload,
    int PayloadVersion, JsonElement? EditedPayload, IReadOnlyList<ProposalWarning> Warnings, ProposalStatus Status,
    Guid? SupersededById, string CreatedVia, Guid? TokenId, PersonId CreatedBy, string? SourceLabel, Instant CreatedAt,
    PersonId? DecidedBy, Instant? DecidedAt, string? DecisionNote, IReadOnlyList<Guid> AppliedRefs, uint Version);

public sealed record SubmitOutcome(StoredProposal Proposal, bool Created);

public static class ProposalCodes
{
    public const string KindUnknown = "core.proposal_kind_unknown";
    public const string Conflict = "core.proposal_conflict";
    public const string Decided = "core.proposal_decided";
    public const string VersionUnsupported = "core.proposal_version_unsupported";

    /// <summary>Warning codes.</summary>
    public const string ApplyFailed = "apply_failed";
    public const string PayloadVersionUnsupported = "payload_version_unsupported";
}

public interface IProposalStore
{
    /// <summary>Deduplicates (same fingerprint pending: returned, not written) and supersedes (same kind, target and subject key pending).</summary>
    Task<SubmitOutcome> SubmitAsync(NewProposal proposal, CancellationToken ct);

    Task<StoredProposal?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>The proposals on <paramref name="targets"/> in <paramref name="status"/>, newest first.</summary>
    Task<IReadOnlyList<StoredProposal>> ListAsync(IReadOnlySet<ItemId> targets, ProposalStatus status, CancellationToken ct);

    /// <summary>`pending` → approved or rejected; <see cref="ProposalCodes.Conflict"/> on a stale version, <see cref="ProposalCodes.Decided"/> when not pending.</summary>
    Task<Result<StoredProposal>> DecideAsync(Guid id, uint version, ProposalStatus to, PersonId by, JsonElement? editedPayload,
        string? note, IReadOnlyList<Guid> appliedRefs, CancellationToken ct);

    /// <summary>Appends one warning in its own write (after a rolled-back approval).</summary>
    Task AddWarningAsync(Guid id, ProposalWarning warning, CancellationToken ct);
}

/// <summary>
/// One per proposal kind, in its module (spec 4C1 D3). Payloads travel as JSON; the handler parses its own type.
/// <see cref="ApplyAsync"/> runs inside the decision's transaction and must not open an <c>IUnitOfWork</c>.
/// </summary>
public interface IProposalHandler
{
    string Module { get; }
    string Kind { get; }
    int PayloadVersion { get; }

    /// <summary>Against the current state: a failure refuses (field errors at <c>payload.*</c>), warnings are kept.</summary>
    Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, CancellationToken ct);

    /// <summary>The current state next to the proposed one, for the decider.</summary>
    Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct);

    /// <summary>Sends the module's command as the current actor; the ids of what it created.</summary>
    Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct);
}
