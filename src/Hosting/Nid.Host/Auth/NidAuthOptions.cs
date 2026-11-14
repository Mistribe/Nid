namespace Nid.Host.Auth;

public sealed class NidAuthOptions
{
    public const string Section = "Auth";

    /// <summary><c>local</c>, <c>oidc</c> or <c>both</c>; unset, inferred from <see cref="Authority"/> (<see cref="AuthSettings"/>).</summary>
    public string? Mode { get; set; }

    /// <summary><c>identity_provider</c> or <c>nid</c>; unset, <c>identity_provider</c> in <c>oidc</c> and <c>nid</c> otherwise.</summary>
    public string? AdminSource { get; set; }

    /// <summary>
    /// Off by default. On, the cookies lose the <c>__Host-</c> prefix and follow the request's scheme, so they travel over
    /// plain HTTP (a home network); the host warns at every start and the admin area shows it (local-accounts spec §12).
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Public origin of the application, e.g. <c>https://nid.example.org</c>: used for redirects and the Origin check.</summary>
    public string PublicUrl { get; set; } = "";

    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "nid";
    public string ClientSecret { get; set; } = "";

    /// <summary>The identity provider's group whose members may use Nid; a sign-in without it is refused.</summary>
    public string AccessGroup { get; set; } = "nid-users";

    /// <summary>The identity provider's group whose members administer Nid; recomputed at every sign-in.</summary>
    public string AdminGroup { get; set; } = "nid-admins";

    /// <summary>Secret used to derive CSRF tokens. At least 32 characters.</summary>
    public string CsrfKey { get; set; } = "";

    public int SessionSlidingDays { get; set; } = 7;
    public int SessionAbsoluteDays { get; set; } = 30;

    /// <summary>The private and loopback ranges: where the hoster's proxy is (Caddy's <c>private_ranges</c>).</summary>
    public static readonly string[] DefaultTrustedNetworks =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "fd00::/8", "::1/128"];

    /// <summary>
    /// Networks whose forwarded headers are trusted (CIDR). Empty means <see cref="DefaultTrustedNetworks"/>; a list
    /// replaces them, e.g. the address of the hoster's proxy alone. <c>0.0.0.0/0</c> and <c>::/0</c> trust any.
    /// </summary>
    public List<string> TrustedNetworks { get; set; } = [];

    /// <summary>Development only: accept the local TLS proxy's certificate for calls to the identity provider.</summary>
    public bool TrustAnyIdentityProviderCertificate { get; set; }
}
