namespace Nid.Core.Contracts;

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

    /// <summary>A TOTP or recovery code that is wrong, used already, or a replay. HTTP 400 on the Security page, 401 at sign-in.</summary>
    public const string InvalidCode = "auth.invalid_code";

    /// <summary>Two-step verification is already on: turn it off first. HTTP 409.</summary>
    public const string TotpAlreadyEnabled = "auth.totp_already_enabled";
}
