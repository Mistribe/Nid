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

    /// <summary>What the admin area's security journal shows (spec §5.1): nothing about another adult's own sessions.</summary>
    public static IReadOnlySet<string> AdminJournal { get; } = new HashSet<string>
    {
        LoginRefused, AccessViolation, PersonPreregistered, PersonPreregistrationCancelled,
        AdminGranted, AdminRevoked, SessionRevoked, HouseholdSettingsChanged,
    };
}

/// <summary>One event. <paramref name="Data"/> holds identifiers only: never a name, an email or a label.</summary>
public sealed record SecurityEvent(string Name, PersonId? Subject, IReadOnlyList<ItemId> Items, IReadOnlyDictionary<string, string> Data)
{
    public static SecurityEvent Of(string name, PersonId? subject) => new(name, subject, [], new Dictionary<string, string>());
}

/// <summary>
/// Written on its own connection, outside the request's transaction, so it survives the failure that caused it
/// (spec U10); a failure to write is logged and never fails the caller (U11).
/// </summary>
public interface ISecurityEvents
{
    /// <summary>As the current actor.</summary>
    Task RecordAsync(SecurityEvent securityEvent, CancellationToken ct);

    /// <summary>As <paramref name="actor"/>, for entry points off the pipeline (the OIDC callback, sign-out).</summary>
    Task RecordAsync(SecurityEvent securityEvent, Actor actor, CancellationToken ct);
}

/// <summary>A command whose success is a security event (spec U13); <c>AuditBehavior</c> records it after success only.</summary>
public interface IRecordsSecurityEvent
{
    SecurityEvent EventFor(object response);
}
