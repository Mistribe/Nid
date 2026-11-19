using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

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

public static class CredentialLinkPurposes
{
    public const string Enroll = "enroll";
    public const string Reset = "reset";
}

/// <summary>Failure codes of local accounts (local-accounts.md §14).</summary>
public static class LocalAccountCodes
{
    /// <summary>Another adult already signs in with this identifier. HTTP 409.</summary>
    public const string LoginTaken = "core.login_taken";

    /// <summary>The identifier is empty, too long, or holds a space or a control character. HTTP 400.</summary>
    public const string LoginInvalid = "auth.login_invalid";

    /// <summary>Shorter than the minimum, longer than 256 characters. HTTP 400.</summary>
    public const string PasswordTooShort = "auth.password_too_short";

    public const string PasswordTooLong = "auth.password_too_long";

    /// <summary>On the embedded list of common passwords, or holding the identifier, the email, the name or « nid ». HTTP 400.</summary>
    public const string PasswordTooCommon = "auth.password_too_common";

    /// <summary>The setup is closed: an adult exists. HTTP 404.</summary>
    public const string SetupClosed = "auth.setup_closed";

    /// <summary>Two-step verification is already on: turn it off first. HTTP 409.</summary>
    public const string TotpAlreadyEnabled = "auth.totp_already_enabled";
}

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
