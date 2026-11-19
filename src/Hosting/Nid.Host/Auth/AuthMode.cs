namespace Nid.Host.Auth;

/// <summary>The ways an adult may sign in (local-accounts spec §2, issue 215).</summary>
public enum AuthMode
{
    /// <summary>An identifier and a password, kept by Nid: no identity provider.</summary>
    Local,

    /// <summary>The identity provider, as before local accounts.</summary>
    Oidc,

    /// <summary>Either, as each adult chooses on the sign-in page.</summary>
    Both,
}

/// <summary>Who decides <c>person.is_admin</c> (local-accounts spec §6).</summary>
public enum AdminSource
{
    /// <summary>The identity provider's admin group, copied at every OIDC sign-in.</summary>
    IdentityProvider,

    /// <summary>The admin area and the CLI.</summary>
    Nid,
}

/// <summary>
/// What <see cref="NidAuthOptions"/> resolve to once their defaults are applied and checked at startup. An unset
/// <c>Auth:Mode</c> is <see cref="AuthMode.Oidc"/> when <c>Auth:Authority</c> is set, so an existing OIDC installation keeps
/// working without a change to its configuration, and <see cref="AuthMode.Local"/> otherwise.
/// </summary>
public sealed record AuthSettings(AuthMode Mode, AdminSource AdminSource, bool AllowInsecureHttp)
{
    public bool Oidc => Mode is AuthMode.Oidc or AuthMode.Both;

    public bool Local => Mode is AuthMode.Local or AuthMode.Both;

    /// <summary>The wire names of the sign-in methods, as <c>/auth/session</c> lists them.</summary>
    public IReadOnlyList<string> Methods => Mode switch
    {
        AuthMode.Local => ["password"],
        AuthMode.Oidc => ["oidc"],
        _ => ["password", "oidc"],
    };

    public string ModeName => Mode.ToString().ToLowerInvariant();

    public string AdminSourceName => AdminSource == AdminSource.IdentityProvider ? "identity_provider" : "nid";

    /// <summary>
    /// Applies the defaults and refuses what cannot work, with a message that names the setting. <paramref name="demo"/>:
    /// demo mode has its own sign-in and ignores these settings, so only the transport is checked.
    /// </summary>
    public static AuthSettings Resolve(NidAuthOptions options, bool demo = false)
    {
        var mode = ParseMode(options.Mode, options.Authority);
        var source = ParseAdminSource(options.AdminSource, mode);
        var settings = new AuthSettings(mode, source, options.AllowInsecureHttp);
        if (!demo)
        {
            if (settings.Oidc && (string.IsNullOrWhiteSpace(options.Authority) || string.IsNullOrWhiteSpace(options.ClientSecret)))
                throw new InvalidOperationException(
                    $"Auth:Mode is {settings.ModeName}: set Auth:Authority and Auth:ClientSecret (the identity provider and Nid's client in it), or use Auth:Mode=local.");
            if (mode == AuthMode.Local && source == AdminSource.IdentityProvider)
                throw new InvalidOperationException(
                    "Auth:AdminSource=identity_provider needs an identity provider: with Auth:Mode=local the admin role is set in Nid (Auth:AdminSource=nid).");
        }
        CheckTransport(options.PublicUrl, options.AllowInsecureHttp);
        return settings;
    }

    private static AuthMode ParseMode(string? value, string? authority)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.IsNullOrWhiteSpace(authority) ? AuthMode.Local : AuthMode.Oidc;
        return value.Trim().ToLowerInvariant() switch
        {
            "local" => AuthMode.Local,
            "oidc" => AuthMode.Oidc,
            "both" => AuthMode.Both,
            _ => throw new InvalidOperationException($"Auth:Mode must be local, oidc or both (got « {value} »)."),
        };
    }

    private static AdminSource ParseAdminSource(string? value, AuthMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
            return mode == AuthMode.Oidc ? AdminSource.IdentityProvider : AdminSource.Nid;
        return value.Trim().ToLowerInvariant() switch
        {
            "identity_provider" => AdminSource.IdentityProvider,
            "nid" => AdminSource.Nid,
            _ => throw new InvalidOperationException($"Auth:AdminSource must be identity_provider or nid (got « {value} »)."),
        };
    }

    /// <summary>
    /// An <c>http://</c> public address only with <c>Auth:AllowInsecureHttp</c>, except the loopback addresses, where
    /// browsers keep a <c>Secure</c> cookie (spec §12). No address (the build-time OpenAPI run) is not checked.
    /// </summary>
    private static void CheckTransport(string publicUrl, bool allowInsecureHttp)
    {
        if (allowInsecureHttp || string.IsNullOrWhiteSpace(publicUrl)
            || !Uri.TryCreate(publicUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp || url.IsLoopback)
            return;
        throw new InvalidOperationException(
            $"Auth:PublicUrl is {publicUrl}: put Nid behind an HTTPS reverse proxy, or set Auth:AllowInsecureHttp=true to let passwords and the session cookie travel unencrypted (a home network only).");
    }
}
