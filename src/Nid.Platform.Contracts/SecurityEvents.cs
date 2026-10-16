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

    /// <summary>What the admin area's security journal shows (spec §5.1): nothing about another adult's own sessions.</summary>
    public static IReadOnlySet<string> AdminJournal { get; } = new HashSet<string>
    {
        LoginRefused, AccessViolation, PersonPreregistered, PersonPreregistrationCancelled,
        AdminGranted, AdminRevoked, SessionRevoked, HouseholdSettingsChanged, AdminCommand, SubjectRebound, TokenAuthFailed,
    };
}

/// <summary>One event. <paramref name="Data"/> holds identifiers only: never a name, an email or a label.</summary>
public sealed record SecurityEvent(string Name, PersonId? Subject, IReadOnlyList<ItemId> Items, IReadOnlyDictionary<string, string> Data)
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
