namespace Nid.Host.Auth;

public sealed class NidAuthOptions
{
    public const string Section = "Auth";

    /// <summary>Public origin of the application, e.g. <c>https://nid.example.org</c>: used for redirects and the Origin check.</summary>
    public string PublicUrl { get; set; } = "";

    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "nid";
    public string ClientSecret { get; set; } = "";

    /// <summary>Secret used to derive CSRF tokens. At least 32 characters.</summary>
    public string CsrfKey { get; set; } = "";

    public int SessionSlidingDays { get; set; } = 7;
    public int SessionAbsoluteDays { get; set; } = 30;

    /// <summary>Development only: accept the local TLS proxy's certificate for calls to the identity provider.</summary>
    public bool TrustAnyIdentityProviderCertificate { get; set; }
}
