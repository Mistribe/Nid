using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nid.Core.Contracts;

namespace Nid.Host.Auth;

/// <summary>
/// Integration tests replace the identity provider with a header naming the acting person
/// (<see cref="IntegrationTestGate.ActorHeader"/>). This scheme is the only place that header is read: it
/// looks the person up and puts the exact claims a real session would carry
/// (<see cref="SessionClaims.Create"/>) onto the principal, so <c>RequireAuthorization()</c> passes and
/// <c>ActorFilter</c>'s ordinary <see cref="SessionClaims.ReadPersonId"/> check already works — no
/// test-specific branch needed there. It also means a real, person-and-epoch-bound CSRF token can be
/// obtained from <c>/auth/session</c> exactly as a browser would, which is what <c>NidApp</c> does instead
/// of bypassing <c>CsrfMiddleware</c>. Registered only when the host environment is exactly
/// <see cref="IntegrationTestGate.Environment"/>, which production never sets (see <c>Program.cs</c>).
/// </summary>
public static class IntegrationTestAuthentication
{
    public static IServiceCollection AddIntegrationTestAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(o => o.DefaultScheme = IntegrationTestGate.Environment)
            .AddScheme<AuthenticationSchemeOptions, IntegrationTestAuthenticationHandler>(IntegrationTestGate.Environment, _ => { });
        return services;
    }
}

public sealed class IntegrationTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IPersonRegistry persons)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue(IntegrationTestGate.ActorHeader, out var raw) && Guid.TryParse(raw, out var id))
        {
            var person = await persons.FindAsync(new PersonId(id), Context.RequestAborted);
            if (person is not null)
                return AuthenticateResult.Success(new AuthenticationTicket(
                    SessionClaims.Create(person, "integration-test", IntegrationTestGate.Environment),
                    IntegrationTestGate.Environment));
        }

        // No usable header: still authenticate — with no person claim — so RequireAuthorization() passes and
        // the pipeline's own ActorBehavior (which requires accessor.Current) is what refuses the request,
        // exactly as it would for any other request with no identified actor.
        var claimless = new ClaimsPrincipal(new ClaimsIdentity(IntegrationTestGate.Environment));
        return AuthenticateResult.Success(new AuthenticationTicket(claimless, IntegrationTestGate.Environment));
    }
}
