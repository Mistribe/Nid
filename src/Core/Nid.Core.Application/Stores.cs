using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Application;

/// <summary>
/// Spec §5: a scenario's document, stored as a JSON row keyed by its registry item. <c>Scope</c> is what it is computed on.
/// <c>Document</c> is set for a purchase, <c>Projection</c> for a projection scenario (projection spec §6), <c>Sale</c> for a sale
/// (issue #416), by <c>Kind</c>.
/// </summary>
public sealed record StoredScenario(
    ItemId Item, string Kind, VisibilityScope Scope, PropertyPurchase? Document, ProjectionScenario? Projection = null, SaleScenario? Sale = null);

public interface IScenarioStore
{
    Task<StoredScenario?> FindAsync(ItemId id, CancellationToken ct);

    /// <summary>The scenarios that exist among <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<StoredScenario>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task SaveAsync(StoredScenario scenario, CancellationToken ct);
}

/// <summary>A stored quote with the dates of its first and last rate.</summary>
public sealed record FxQuoteSpan(Currency Quote, LocalDate First, LocalDate Last);

public interface IFxRateStore
{
    /// <summary>Writes by priority (<see cref="FxRateSource"/>); returns the rows inserted or replaced.</summary>
    Task<int> WriteAsync(IReadOnlyList<FxRate> rates, CancellationToken ct);

    /// <summary>Every stored rate of <paramref name="quotes"/> with <c>from ≤ on_date ≤ to</c>.</summary>
    Task<IReadOnlyList<FxRate>> ReadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct);

    /// <summary>For each quote, the latest stored rate strictly before <paramref name="date"/> (plausibility, design §4.4).</summary>
    Task<IReadOnlyDictionary<Currency, decimal>> PreviousAsync(IReadOnlyCollection<Currency> quotes, LocalDate date, CancellationToken ct);

    /// <summary>Every stored quote with its first and last rate date, by quote code.</summary>
    Task<IReadOnlyList<FxQuoteSpan>> QuotesAsync(CancellationToken ct);

    /// <summary>The dates on or after <paramref name="from"/> holding at least one rate of <paramref name="source"/>.</summary>
    Task<IReadOnlySet<LocalDate>> DatesFromAsync(FxRateSource source, LocalDate from, CancellationToken ct);
}

public enum FxRunKind
{
    Daily,
    Backfill,
}

public enum FxRunStatus
{
    Running,
    Succeeded,
    Failed,
}

public sealed record FxSyncRun(
    Guid Id,
    FxRunKind Kind,
    Instant StartedAt,
    Instant? FinishedAt,
    FxRunStatus Status,
    int FilesRead,
    int RowsWritten,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<FxRejection> Rejected)
{
    // The compiler-generated equality would compare Rejected by reference (IReadOnlyList<T> has none of its own):
    // two runs read back from storage would never equal one another. Compare it by content instead.
    public bool Equals(FxSyncRun? other)
        => other is not null
           && Id == other.Id && Kind == other.Kind && StartedAt == other.StartedAt && FinishedAt == other.FinishedAt
           && Status == other.Status && FilesRead == other.FilesRead && RowsWritten == other.RowsWritten
           && FailureCode == other.FailureCode && FailureMessage == other.FailureMessage
           && Rejected.SequenceEqual(other.Rejected);

    public override int GetHashCode()
        => HashCode.Combine(Id, Kind, StartedAt, FinishedAt, Status, FilesRead, HashCode.Combine(RowsWritten, FailureCode, FailureMessage));
}

/// <summary>The collection journal, <c>core.fx_sync_run</c> (design §4.2).</summary>
public interface IFxRunLog
{
    /// <summary>
    /// Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row.
    /// A run still marked running an hour after it started is closed as <see cref="FxCodes.RunAbandoned"/> first,
    /// so a crash never blocks collection for ever.
    /// </summary>
    Task<FxSyncRun?> TryStartAsync(FxRunKind kind, CancellationToken ct);

    Task FinishAsync(FxSyncRun run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>Whether a run of <paramref name="kind"/> has ever succeeded (the purge keeps the latest succeeded backfill).</summary>
    Task<bool> HasSucceededAsync(FxRunKind kind, CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<FxSyncRun>> RecentAsync(int count, CancellationToken ct);

    /// <summary>One page of runs, newest first, and the total number of runs.</summary>
    Task<(IReadOnlyList<FxSyncRun> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>, except the latest succeeded backfill (<see cref="HasSucceededAsync"/>).</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>A source file's bytes, or <c>null</c> when SubTracker has no file that day (an HTTP 404; the ECB history never is).</summary>
public sealed record FxFetch(byte[]? Bytes);

/// <summary>The two rate sources (design §4.3). Failures carry <see cref="FxCodes"/> source codes; nothing throws.</summary>
public interface IFxSourceClient
{
    Task<UnambitiousFx.Functional.Result<FxFetch>> SubTrackerDayAsync(LocalDate date, CancellationToken ct);

    Task<UnambitiousFx.Functional.Result<FxFetch>> EcbHistoryAsync(CancellationToken ct);
}

/// <summary>Starts the backfill job now (Quartz in the host); fails with <see cref="FxCodes.JobsDisabled"/> when jobs are off.</summary>
public interface IFxJobTrigger
{
    Task<UnambitiousFx.Functional.Result> TriggerBackfillAsync(CancellationToken ct);
}

/// <summary>Fetches the raw SDMX document of the INSEE series through the outbound client; never throws, a failure is an <see cref="InflationCodes"/> code.</summary>
public interface ICpiSourceClient
{
    Task<Result<byte[]>> FetchAsync(CancellationToken ct);
}

/// <summary>Starts the <c>cpi-weekly</c> job now (Quartz in the host); <see cref="InflationCodes.JobsDisabled"/> when jobs are off.</summary>
public interface ICpiJobTrigger
{
    Task<Result> TriggerAsync(CancellationToken ct);
}

public interface ICpiRunLog
{
    /// <summary>Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row. A run still running an hour on is closed as failed first.</summary>
    Task<CpiRunView?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(CpiRunView run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<CpiRunView>> RecentAsync(int count, CancellationToken ct);

    /// <summary>One page of runs, newest first, and the total number of runs.</summary>
    Task<(IReadOnlyList<CpiRunView> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>The stored CPI series, <c>core.cpi</c>, one value per month.</summary>
public interface ICpiStore
{
    /// <summary>Every stored value, by month.</summary>
    Task<IReadOnlyList<CpiValue>> ListAsync(CancellationToken ct);

    /// <summary>One page of values, newest month first, and the total number of values.</summary>
    Task<(IReadOnlyList<CpiValue> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Stores the month's value, replacing the one stored (the collector decides what may replace what).</summary>
    Task UpsertAsync(CpiValue value, CancellationToken ct);

    /// <summary>Stores the values in one transaction, replacing the ones stored: a whole collection lands or none of it does.</summary>
    Task UpsertManyAsync(IReadOnlyList<CpiValue> values, CancellationToken ct);

    /// <summary>Stores a manual value in one statement unless the month holds an <c>insee</c> one; false when it did not write.</summary>
    Task<bool> UpsertManualAsync(CpiValue value, CancellationToken ct);
}

/// <summary>
/// An adult's local account (identity-and-access/local-accounts.md §13.1): the identifier, the password hash in the
/// hasher's own self-describing format, and the lock state. Never on the wire, never in a log or an export.
/// </summary>
public sealed record LocalCredential(
    PersonId Person, string Login, string PasswordHash, int FailedCount, int LockLevel, Instant? LockedUntil, Instant PasswordChangedAt = default,
    string? TotpSecret = null, Instant? TotpPendingSince = null, Instant? TotpEnabledAt = null)
{
    /// <summary>The second factor is on (§10): a code is asked after the password.</summary>
    public bool TotpEnabled => TotpEnabledAt is not null && TotpSecret is not null;
}

/// <summary>What a failed attempt did to the account: still open, or locked until <paramref name="Until"/> at that level.</summary>
public sealed record FailureOutcome(bool LockedNow, int Level, Instant? Until);

/// <summary>
/// A one-time password link as its holder sees it before using it (spec §7): whose it is and what it does. <c>enroll</c>
/// creates the local account of an adult who has none, <c>reset</c> replaces the password of one who has.
/// </summary>
public sealed record CredentialLinkInfo(Guid Id, PersonId Person, string Purpose, string DisplayName, string? Email);

/// <summary>A link just created: the secret, shown once, and when it stops working.</summary>
public sealed record NewCredentialLink(Guid Id, string Token, string Purpose, Instant ExpiresAt);

/// <summary>The local-account side of an adult, as the admin area lists it: never the identifier's secret parts, never a hash.</summary>
public sealed record LocalAccountState(PersonId Person, bool HasPassword, Instant? LockedUntil, Instant? LinkExpiresAt, bool TotpEnabled = false);

/// <summary>What using a link did: the adult, the purpose and the link's id (for the security event).</summary>
public sealed record CompletedLink(PersonInfo Person, string Purpose, Guid LinkId);

/// <summary>
/// <c>core.local_credential</c> and <c>core.credential_link</c> (local-accounts.md §13). Hashing is the caller's: this store
/// only keeps what it is given. Every write that creates an adult or turns one active takes the provisioning lock.
/// </summary>
public interface ILocalAccountStore
{
    /// <summary>The account whose identifier is <paramref name="login"/>, ignoring case; <c>null</c> when none is.</summary>
    Task<LocalCredential?> FindByLoginAsync(string login, CancellationToken ct);

    Task<LocalCredential?> FindAsync(PersonId person, CancellationToken ct);

    /// <summary>
    /// Counts a failure under the account's row lock. At <paramref name="threshold"/> consecutive failures the account locks
    /// for <c>base × 2^(level − 1)</c>, capped at <paramref name="max"/>, and the count starts again.
    /// </summary>
    Task<FailureOutcome> RecordFailureAsync(PersonId person, Instant now, int threshold, NodaTime.Duration lockBase, NodaTime.Duration max, CancellationToken ct);

    /// <summary>A sign-in that passed: the count, the level and the lock are cleared; <paramref name="rehash"/>, when set, replaces the hash.</summary>
    Task RecordSuccessAsync(PersonId person, Instant now, string? rehash, CancellationToken ct);

    /// <summary>Clears the lock and the count; <c>false</c> when the adult has no local account.</summary>
    Task<bool> UnlockAsync(PersonId person, CancellationToken ct);

    /// <summary>
    /// A new link for an adult (spec §7): <c>enroll</c> when they have no local account, <c>reset</c> otherwise. It revokes the
    /// adult's previous live link. Not found for anyone but an adult.
    /// </summary>
    Task<Result<NewCredentialLink>> CreateLinkAsync(PersonId person, PersonId? createdBy, string via, NodaTime.Duration lifetime, CancellationToken ct);

    /// <summary>Revokes the adult's live link; <c>false</c> when there was none.</summary>
    Task<bool> RevokeLinkAsync(PersonId person, CancellationToken ct);

    /// <summary>The live link (not used, not revoked, not expired) with this secret; <c>null</c> otherwise, whichever the reason.</summary>
    Task<CredentialLinkInfo?> FindLinkAsync(string token, Instant now, CancellationToken ct);

    /// <summary>
    /// Uses a live link, in one transaction: an <c>enroll</c> link creates the account with <paramref name="login"/> (the adult
    /// becomes active); a <c>reset</c> link replaces the hash, ends every session (<c>session_epoch</c>) and clears the lock.
    /// Not found when the link is not live; <see cref="LocalAccountCodes.LoginTaken"/> when the identifier is another's.
    /// </summary>
    Task<Result<CompletedLink>> CompleteLinkAsync(string token, string? login, string passwordHash, Instant now, CancellationToken ct);

    /// <summary>
    /// The setup of a fresh instance (spec §5): creates the first adult, admin, with their account, unless an adult exists
    /// (<see cref="LocalAccountCodes.SetupClosed"/>). Under the provisioning lock, so two setups cannot both pass.
    /// </summary>
    Task<Result<PersonInfo>> CreateFirstAdminAsync(string displayName, string login, string passwordHash, CancellationToken ct);

    /// <summary>
    /// The adult replaces their own password (spec §8): the hash, the change time, and <c>session_epoch</c> + 1 so their
    /// other sessions close; the caller reissues the current one. Not found without a local account.
    /// </summary>
    Task<Result<PersonInfo>> ChangePasswordAsync(PersonId person, string passwordHash, Instant now, CancellationToken ct);

    /// <summary>`nid admin create-admin`: a new pending adult who is admin, within the household's cap.</summary>
    Task<Result<PersonInfo>> CreateAdminAdultAsync(string displayName, string? email, CancellationToken ct);

    /// <summary>The local-account state of every adult who has an account or a live link.</summary>
    Task<IReadOnlyList<LocalAccountState>> StatesAsync(Instant now, CancellationToken ct);

    /// <summary>Replaces the password hash only: a rehash at sign-in when the code step still has to pass.</summary>
    Task RehashAsync(PersonId person, string passwordHash, CancellationToken ct);

    /// <summary>
    /// Keeps a new TOTP secret, encrypted by the caller, waiting for its first code (spec §10). <c>false</c> without a local
    /// account or when the second factor is already on.
    /// </summary>
    Task<bool> BeginTotpAsync(PersonId person, string protectedSecret, Instant now, CancellationToken ct);

    /// <summary>
    /// Turns the waiting secret on, with the step its first code was of, and replaces the recovery codes by these hashes.
    /// <c>false</c> when no secret waits or it waited since before <paramref name="pendingSince"/>.
    /// </summary>
    Task<bool> ConfirmTotpAsync(PersonId person, long step, IReadOnlyCollection<string> recoveryCodeHashes, Instant now, Instant pendingSince, CancellationToken ct);

    /// <summary>Accepts a code of time step <paramref name="step"/> once: <c>false</c> for that step or an earlier one again (a replay).</summary>
    Task<bool> AcceptTotpStepAsync(PersonId person, long step, CancellationToken ct);

    /// <summary>Uses a recovery code by its hash, once: the codes left, or <c>null</c> when none matched.</summary>
    Task<int?> UseRecoveryCodeAsync(PersonId person, string codeHash, Instant now, CancellationToken ct);

    /// <summary>New recovery codes, the previous ones gone; <c>false</c> when the second factor is off.</summary>
    Task<bool> ReplaceRecoveryCodesAsync(PersonId person, IReadOnlyCollection<string> codeHashes, Instant now, CancellationToken ct);

    Task<int> RecoveryCodesLeftAsync(PersonId person, CancellationToken ct);

    /// <summary>
    /// Turns the second factor off and erases its recovery codes; with <paramref name="endSessions"/> (<c>nid admin
    /// reset-totp</c>) every session of the adult closes too. Not found without a local account.
    /// </summary>
    Task<Result<PersonInfo>> DisableTotpAsync(PersonId person, bool endSessions, CancellationToken ct);
}

/// <summary>What the feed route needs of a calendar row: never sent anywhere.</summary>
public sealed record CalendarCredential(Guid Id, PersonId Person, string Hash, CalendarFeedOptions Options, Instant ExpiresAt, Instant? RevokedAt);

public sealed record PersonalAccessToken(
    Guid Id, PersonId Person, string Name, string Prefix, IReadOnlySet<string> Scopes,
    Instant CreatedAt, Instant ExpiresAt, Instant? LastUsedAt, Instant? RevokedAt, CalendarFeedOptions? Calendar = null);

/// <summary>What authentication needs of a row: never the name, never sent anywhere.</summary>
public sealed record TokenCredential(Guid Id, PersonId Person, string Hash, IReadOnlySet<string> Scopes, Instant ExpiresAt, Instant? RevokedAt);

/// <summary>A token just created: <see cref="Secret"/> exists here and nowhere else.</summary>
public sealed record NewToken(PersonalAccessToken Token, string Secret);

public interface IPersonalAccessTokens
{
    /// <summary>Validates name, preset and expiry (field failures), then creates under a lock on the person (10 active at most).</summary>
    Task<Result<NewToken>> CreateAsync(PersonId person, string name, string preset, int? expiresInDays, CancellationToken ct);

    /// <summary>Every token of the person, newest first, revoked and expired ones included.</summary>
    Task<IReadOnlyList<PersonalAccessToken>> ListAsync(PersonId person, CancellationToken ct);

    /// <summary><c>true</c> when an active token was revoked, <c>false</c> when it already was not active; not found for another's.</summary>
    Task<Result<bool>> RevokeAsync(PersonId person, Guid token, CancellationToken ct);

    /// <summary>Revokes every active token of the person; the count revoked.</summary>
    Task<int> RevokeAllAsync(PersonId person, CancellationToken ct);

    /// <summary>A bearer token's row; never a calendar feed's.</summary>
    Task<TokenCredential?> FindByPrefixAsync(string prefix, CancellationToken ct);

    /// <summary>
    /// Issue #80: creates the person's calendar feed under the same lock and limit, revoking the active one first (a regeneration).
    /// Its secret is a <c>nid_cal_…</c>, which bearer authentication never reads.
    /// </summary>
    Task<Result<NewToken>> CreateCalendarAsync(PersonId person, CalendarFeedOptions options, CancellationToken ct);

    /// <summary>Changes the active feed's options, its URL unchanged; not found when the person has none.</summary>
    Task<Result<PersonalAccessToken>> SetCalendarOptionsAsync(PersonId person, CalendarFeedOptions options, CancellationToken ct);

    Task<CalendarCredential?> FindCalendarByPrefixAsync(string prefix, CancellationToken ct);

    /// <summary>Sets <c>last_used_at</c> to now when it is unset or older than a minute (spec D3).</summary>
    Task TouchAsync(Guid token, CancellationToken ct);
}

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

public sealed record ProposalPage(IReadOnlyList<StoredProposal> Items, int Total);

public sealed record SubmitOutcome(StoredProposal Proposal, bool Created, IReadOnlyList<Guid>? Superseded = null);

public interface IProposalStore
{
    /// <summary>Deduplicates (same fingerprint pending: returned, not written) and supersedes (same kind, target and subject key pending).</summary>
    Task<SubmitOutcome> SubmitAsync(NewProposal proposal, CancellationToken ct);

    Task<StoredProposal?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// The proposals on <paramref name="targets"/>, and those without a target <paramref name="person"/> submitted (none when null), in
    /// <paramref name="status"/>, newest first.
    /// </summary>
    /// <summary>The proposals on <paramref name="targets"/> (and <paramref name="person"/>'s untargeted ones). With a
    /// <paramref name="viewer"/>, another adult's pending assistant proposals are left out (#498, decision N).</summary>
    Task<IReadOnlyList<StoredProposal>> ListAsync(
        IReadOnlySet<ItemId> targets, PersonId? person, ProposalStatus status, CancellationToken ct, PersonId? viewer = null);

    /// <summary>
    /// #774: <see cref="ListAsync"/> in SQL, narrowed to <paramref name="search"/> (part of the source label or of the target's name,
    /// whatever the case), <paramref name="take"/> rows from <paramref name="skip"/> (all when null), with the total before the page.
    /// With <paramref name="writable"/>, the pending ones the viewer cannot decide are left out: on a target outside it, or a consent
    /// request (<see cref="PrivateRequestProposal"/>) the viewer made.
    /// </summary>
    Task<ProposalPage> PageAsync(IReadOnlySet<ItemId> targets, PersonId? person, ProposalStatus status, PersonId viewer,
        string? search, int skip, int? take, CancellationToken ct, IReadOnlySet<ItemId>? writable = null);

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

/// <summary>
/// Decision N (<c>docs/ai/README.md</c> §8.2, #498): a proposal the assistant made for a person stays that person's until it
/// is decided, even on a shared item; it merges with and replaces only that person's own pending assistant proposals.
/// </summary>
public static class AssistantProposals
{
    public const string Via = "assistant";

    /// <summary>Hidden from <paramref name="viewer"/>: pending, made by the assistant, for someone else.</summary>
    public static bool HiddenFrom(StoredProposal proposal, PersonId viewer)
        => proposal.Status == ProposalStatus.Pending && proposal.CreatedVia == Via && proposal.CreatedBy != viewer;
}
