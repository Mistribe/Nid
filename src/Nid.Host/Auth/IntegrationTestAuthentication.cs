using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Nid.Host.Auth;

/// <summary>
/// Integration tests replace the identity provider with a header naming the acting person
/// (see <c>ActorFilter</c> in <c>Nid.Host.Api.PersonEndpoints</c>), but the pipeline still runs
/// <c>RequireAuthorization()</c> exactly as in production. This scheme exists only to give that check an
/// authenticated — but claimless — principal to pass, so <c>ActorFilter</c> gets a chance to run and read
/// the test header itself. Registered only when the host environment is exactly <c>IntegrationTest</c>,
/// which production never sets (see <c>Program.cs</c>).
/// </summary>
public static class IntegrationTestAuthentication
{
    public const string Scheme = "IntegrationTest";

    public static IServiceCollection AddIntegrationTestAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(o => o.DefaultScheme = Scheme)
            .AddScheme<AuthenticationSchemeOptions, IntegrationTestAuthenticationHandler>(Scheme, _ => { });
        return services;
    }
}

public sealed class IntegrationTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(IntegrationTestAuthentication.Scheme));
        var ticket = new AuthenticationTicket(principal, IntegrationTestAuthentication.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
