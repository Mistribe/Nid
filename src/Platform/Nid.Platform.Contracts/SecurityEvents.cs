using Nid.Core.Contracts;

namespace Nid.Platform.Contracts;

/// <summary>The security events of 1F3a (administration-and-audit.md §4.1, spec §5).</summary>
public static class SecurityEventNames
{
    public const string Login = "login";
    public const string Logout = "logout";
    public const string LoginRefused = "login_refused";
    public const string AccessDenied = "access_denied";
    public const string AccessViolation = "access_violation";
    public const string PersonPreregistered = "person_preregistered";
    public const string PersonPreregistrationCancelled = "person_preregistration_cancelled";
    public const string AdminGranted = "admin_granted";
    public const string AdminRevoked = "admin_revoked";
    public const string SessionRevoked = "session_revoked";
    public const string HouseholdSettingsChanged = "household_settings_changed";
    public const string AdminCommand = "admin_command";
    public const string SubjectRebound = "subject_rebound";
    public const string TokenCreated = "token_created";
    public const string TokenRevoked = "token_revoked";
    public const string TokenAuthFailed = "token_auth_failed";

    /// <summary>4B: one per MCP tool call (spec §4); not in the admin journal — an adult's own reads.</summary>
    public const string McpToolCalled = "mcp_tool_called";

    /// <summary>5B: a bank statement file written to an account (bank sync spec §10); counts only, never a label, an amount or an IBAN.</summary>
    public const string BankStatementImported = "bank_statement_imported";

    /// <summary>
    /// Issue 215, local-accounts.md §16: a wrong password or code, a locked account, an unknown identifier (never its text);
    /// at most one a minute per account or address, with <c>count</c>. Data <c>reason</c>, <c>count</c>, <c>address</c>.
    /// </summary>
    public const string LoginFailed = "login_failed";

    /// <summary>Issue 215: a local account locked after repeated failures (<c>level</c>, <c>until</c>).</summary>
    public const string LoginLocked = "login_locked";

    /// <summary>Issue 215: the adult changed their password, or their identifier (<c>field</c>).</summary>
    public const string PasswordChanged = "password_changed";

    /// <summary>Issue 215: a reset link was used; the adult's sessions are closed (<c>link_id</c>).</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>Issue 215: an admin or the CLI created or revoked a one-time password link (<c>purpose</c>, <c>link_id</c>, <c>by</c>).</summary>
    public const string CredentialLinkIssued = "credential_link_issued";

    public const string CredentialLinkRevoked = "credential_link_revoked";

    /// <summary>Issue 215: an enrolment link was used: the adult is active (<c>link_id</c>).</summary>
    public const string AccountEnrolled = "account_enrolled";

    /// <summary>Issue 215: an admin or the CLI unlocked a local account (<c>by</c>).</summary>
    public const string AccountUnlocked = "account_unlocked";

    /// <summary>Issue 215: the first administrator was created on the setup page.</summary>
    public const string SetupCompleted = "setup_completed";

    /// <summary>What the admin area's security journal shows (spec §5.1): nothing about another adult's own sessions.</summary>
    public static IReadOnlySet<string> AdminJournal { get; } = new HashSet<string>
    {
        LoginRefused, AccessViolation, PersonPreregistered, PersonPreregistrationCancelled,
        AdminGranted, AdminRevoked, SessionRevoked, HouseholdSettingsChanged, AdminCommand, SubjectRebound, TokenAuthFailed,
        // Issue 215: what an admin needs to act on; an adult's own failed sign-ins stay theirs (login_failed is not here).
        LoginLocked, PasswordReset, CredentialLinkIssued, CredentialLinkRevoked, AccountEnrolled, AccountUnlocked, SetupCompleted,
    };
}

/// <summary>One event. <paramref name="Data"/> holds identifiers only: never a name, an email or a label. <paramref name="Id"/> is the audit row's id when the caller needs it beforehand.</summary>
public sealed record SecurityEvent(string Name, PersonId? Subject, IReadOnlyList<ItemId> Items, IReadOnlyDictionary<string, string> Data, Guid? Id = null)
{
    public static SecurityEvent Of(string name, PersonId? subject) => new(name, subject, [], new Dictionary<string, string>());
}

/// <summary>
/// Written on its own connection, outside the request's transaction, so it survives the failure that caused it
/// (spec U10); a failure to write is logged and never fails the caller (U11). The <c>bool</c> tells the caller
/// whether the row actually landed, so a caller keeping its own state (<see cref="Nid.Platform.Audit.AccessDeniedThrottle"/>)
/// can undo it on a failed write.
/// </summary>
public interface ISecurityEvents
{
    /// <summary>As the current actor.</summary>
    Task<bool> RecordAsync(SecurityEvent securityEvent, CancellationToken ct);

    /// <summary>As <paramref name="actor"/>, for entry points off the pipeline (the OIDC callback, sign-out).</summary>
    Task<bool> RecordAsync(SecurityEvent securityEvent, Actor actor, CancellationToken ct);
}

/// <summary>
/// A command whose success is a security event (spec U13); <c>AuditBehavior</c> records it after success only.
/// A null return (Minor #4) means this particular success is not worth an event — e.g. a household-settings
/// update that named no field.
/// </summary>
public interface IRecordsSecurityEvent
{
    SecurityEvent? EventFor(object response);
}

/// <summary>
/// Spec U12: one <c>access_denied</c> per (actor, item, minute) — the item-access behavior's refusals and, from 4D1, a
/// proposal read or decision refused by its target's rights.
/// </summary>
public interface IAccessDenials
{
    /// <summary><paramref name="access"/> is <c>read</c>, <c>write</c> or <c>create</c>; <paramref name="item"/> is null for a creation.</summary>
    Task RecordAsync(PersonId person, ItemId? item, string access, CancellationToken ct);
}
