using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Nid.Core.Contracts;

namespace Nid.Host.Auth;

public static class AuthenticationSetup
{
    public static IServiceCollection AddNidAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(NidAuthOptions.Section);
        services.Configure<NidAuthOptions>(section);
        var options = section.Get<NidAuthOptions>() ?? new NidAuthOptions();

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            // The reverse proxy is the host's responsibility (architecture.md §10.2): trust what it forwards.
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            // Empty lists trust any proxy; the reference compose names the network nid-web lives on (1F1 design §2.3).
            foreach (var network in options.TrustedNetworks)
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });

        services.AddAuthentication(o =>
            {
                // 4A: a bearer token is chosen per request; everything else stays the cookie's.
                o.DefaultScheme = TokenAuthentication.Policy;
                // API calls get a plain 401 from the cookie scheme; only /auth/login challenges the identity provider.
                o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // The OIDC callback signs in to the cookie whatever headers it carries.
                o.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddPolicyScheme(TokenAuthentication.Policy, TokenAuthentication.Policy, policy =>
                policy.ForwardDefaultSelector = context => TokenAuthentication.IsBearer(context.Request)
                    ? TokenAuthentication.Scheme
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthentication.Scheme, _ => { })
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "__Host-nid";
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(options.SessionSlidingDays);
                cookie.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
                cookie.Events.OnValidatePrincipal = RejectRevokedSessions;
            })
            .AddOpenIdConnect(oidc =>
            {
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.CallbackPath = "/auth/callback";
                oidc.SaveTokens = false;
                oidc.MapInboundClaims = false;
                // Authelia serves profile and email through userinfo, not the ID token; it announces pushed
                // authorization requests that the handler would use and fail (client authentication), so they stay off.
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("profile");
                oidc.Scope.Add("email");
                if (options.TrustAnyIdentityProviderCertificate)
                    oidc.BackchannelHttpHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                oidc.Events.OnTicketReceived = ProvisionAndReplacePrincipal;
            });

        services.AddAuthorization();
        return services;
    }

    private static async Task RejectRevokedSessions(CookieValidatePrincipalContext context)
    {
        var person = SessionClaims.ReadPersonId(context.Principal!);
        var epoch = SessionClaims.ReadEpoch(context.Principal!);
        if (person is null || epoch is null)
        {
            context.RejectPrincipal();
            return;
        }

        var current = await context.HttpContext.RequestServices.GetRequiredService<IPersonRegistry>()
            .FindAsync(person.Value, context.HttpContext.RequestAborted);
        if (current is null || current.SessionEpoch != epoch)
            context.RejectPrincipal();
    }

    private static async Task ProvisionAndReplacePrincipal(TicketReceivedContext context)
    {
        var claims = context.Principal!;
        var subject = claims.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            context.Fail("The identity provider returned no subject.");
            return;
        }

        var name = claims.FindFirst("name")?.Value ?? claims.FindFirst("preferred_username")?.Value ?? subject;
        var email = claims.FindFirst("email")?.Value;

        var services = context.HttpContext.RequestServices;
        var result = await services.GetRequiredService<OidcSignIn>().CompleteAsync(subject, name, email, context.HttpContext.RequestAborted);

        if (!result.TryGetValue(out var person))
        {
            result.TryGetFailure(out var failure);
            if (failure?.Code != PersonCodes.NotRegistered)
            {
                // Anything else (a database hiccup, a bug) is a real sign-in failure: it must not be mistaken for
                // "ask an administrator", which promises the identity only needs pre-registering.
                context.Fail("Sign-in could not be completed.");
                return;
            }

            // Not registered: the page tells the person to ask an administrator, with the email Nid received so
            // the administrator can match it (authentication.md §5). The web page renders it as text.
            context.HandleResponse();
            context.Response.Redirect(email is null ? "/login-refused" : "/login-refused?email=" + Uri.EscapeDataString(email));
            return;
        }

        context.Principal = SessionClaims.Create(person, subject, CookieAuthenticationDefaults.AuthenticationScheme);
        context.Properties!.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NidAuthOptions>>().Value.SessionAbsoluteDays);
        context.Properties.IsPersistent = true;
    }
}
