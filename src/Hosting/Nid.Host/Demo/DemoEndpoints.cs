using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using Nid.Platform.Contracts;

namespace Nid.Host.Demo;

public static class DemoEndpoints
{
    /// <summary>The web page that offers the demo adults (<c>DemoPage</c>); <c>/auth/login</c> leads there in demo mode.</summary>
    public const string PagePath = "/demo";

    /// <summary>
    /// Demo mode only, mapped nowhere else: signs the browser in as one adult of the demo household, with the same cookie
    /// and claims as an OIDC sign-in (<see cref="SessionClaims.Create"/>), then goes back to <paramref name="returnUrl"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapDemo(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/auth/demo/{key}", async (string key, string? returnUrl, HttpContext http, IPersonRegistry persons,
                ISecurityEvents events, IOptions<NidAuthOptions> options) =>
            {
                var target = returnUrl ?? "/";
                if (!ReturnUrl.IsSafe(target))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a relative path.");
                if (DemoHousehold.Find(key) is not { } adult || await persons.FindBySubjectAsync(adult.Subject, http.RequestAborted) is not { } person)
                    return Results.NotFound();

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    SessionClaims.Create(person, adult.Subject, CookieAuthenticationDefaults.AuthenticationScheme),
                    new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(options.Value.SessionAbsoluteDays) });
                await events.RecordAsync(SecurityEvent.Of(SecurityEventNames.Login, person.Id), Actor.Browser(person.Id, person.IsAdmin), http.RequestAborted);
                return Results.Redirect(target);
            })
            .ExcludeFromDescription();
        return routes;
    }
}

/// <summary>
/// Demo mode only: what would reach outside the demo is refused, whoever asks. Access tokens (an assistant could keep
/// reading the instance), webhooks and channel tests (the server would post to an address a visitor typed).
/// </summary>
public sealed class DemoGuardMiddleware(RequestDelegate next)
{
    private static readonly (string Method, string Path)[] Refused =
    [
        ("POST", "/api/v1/core/me/tokens"),
        ("PUT", "/api/v1/platform/notifications/discord"),
        ("POST", "/api/v1/platform/notifications/test"),
        ("PUT", "/api/v1/admin/notifications/discord"),
        ("POST", "/api/v1/admin/notifications/test"),
    ];

    public static bool IsRefused(string method, string path)
        => Refused.Any(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsRefused(context.Request.Method, context.Request.Path.Value ?? ""))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not available in the demo.",
                detail: "The demo does not create access tokens, webhooks or test messages.").ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
