using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>
/// #291, local-accounts.md §8.1, §9.1, §16: what the holder of a local account is told about it, so a takeover does not go unnoticed.
/// Read off the security events themselves, to the holder only, never a secret, a link or a typed identifier.
/// </summary>
public static class AccountNotices
{
    public const string LoginLocked = "login_locked";
    public const string PasswordLinkIssued = "password_link_issued";
    public const string PasswordReset = "password_reset";
    public const string PasswordChanged = "password_changed";
    public const string TotpReset = "totp_reset";
    public const string RecoveryCodesLow = "recovery_codes_low";

    /// <summary>The recovery codes left at which the holder is told to make new ones; the Security page warns at the same count.</summary>
    public const string CodesLeftWarning = "2";

    /// <summary>Critical ones go out at once and end when read; the low codes stay until new ones are made. Each opens the Security page.</summary>
    public static readonly IReadOnlyList<NotificationKind> Kinds =
    [
        Declared(LoginLocked, NotificationLevel.Critical, "Votre compte a été verrouillé",
            "Après plusieurs essais de mot de passe incorrects. Si ce n'était pas vous, prévenez un administrateur."),
        Declared(PasswordLinkIssued, NotificationLevel.Critical, "Un lien de réinitialisation a été créé pour vous",
            "Un administrateur peut ainsi choisir un nouveau mot de passe pour votre compte. Si vous ne l'avez pas demandé, parlez-en avec lui."),
        Declared(PasswordReset, NotificationLevel.Critical, "Votre mot de passe a été réinitialisé",
            "Vos autres sessions sont fermées. Si ce n'était pas vous, prévenez un administrateur."),
        Declared(PasswordChanged, NotificationLevel.Critical, "Votre mot de passe a changé",
            "Vos autres sessions sont fermées. Si ce n'était pas vous, prévenez un administrateur."),
        Declared(TotpReset, NotificationLevel.Critical, "Votre validation en deux étapes a été désactivée",
            "Un administrateur l'a désactivée depuis le serveur. Réactivez-la depuis la page Sécurité."),
        Declared(RecoveryCodesLow, NotificationLevel.Action, "Il vous reste 2 codes de secours",
            "Créez-en de nouveaux depuis la page Sécurité avant d'en manquer."),
    ];

    private static NotificationKind Declared(string kind, NotificationLevel level, string title, string body)
        => new(kind, NotificationFamilies.Security, level, "platform", null)
        {
            Personal = true,
            Template = new(title) { Notice = true, Body = _ => body, Link = _ => "/security" },
        };

    /// <summary>The notice an event gives its subject, if any; <paramref name="eventId"/> makes each occurrence its own.</summary>
    public static Notice? For(SecurityEvent e, Guid eventId)
    {
        if (e.Subject is not { } holder)
            return null;
        var kind = e.Name switch
        {
            SecurityEventNames.LoginLocked => LoginLocked,
            SecurityEventNames.CredentialLinkIssued when e.Data.GetValueOrDefault("purpose") == CredentialLinkPurposes.Reset => PasswordLinkIssued,
            SecurityEventNames.PasswordReset => PasswordReset,
            SecurityEventNames.PasswordChanged => PasswordChanged,
            SecurityEventNames.TotpReset => TotpReset,
            SecurityEventNames.RecoveryCodeUsed when e.Data.GetValueOrDefault("left") == CodesLeftWarning => RecoveryCodesLow,
            _ => null,
        };
        if (kind is null)
            return null;
        var key = kind == RecoveryCodesLow ? LowCodesKey(holder) : $"{kind}:{holder.Value:D}:{eventId:D}";
        var data = e.Data.Where(d => d.Key is "field" or "left").ToDictionary(d => d.Key, d => d.Value);
        return new Notice(kind, new Recipients.People([holder]), key, [], data);
    }

    /// <summary>The key an event ends: new codes, or no second factor any more, make the low-codes warning moot.</summary>
    public static string? Resolves(SecurityEvent e)
        => e.Subject is { } holder && e.Name is SecurityEventNames.RecoveryCodesRegenerated or SecurityEventNames.TotpDisabled or SecurityEventNames.TotpReset
            ? LowCodesKey(holder)
            : null;

    private static string LowCodesKey(PersonId holder) => $"{RecoveryCodesLow}:{holder.Value:D}";
}
