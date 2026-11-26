using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
                ISecurityEvents events) =>
            {
                var target = returnUrl ?? "/";
                if (!ReturnUrl.IsSafe(target))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "returnUrl must be a relative path.");
                if (DemoHousehold.Find(key) is not { } adult || await persons.FindBySubjectAsync(adult.Subject, http.RequestAborted) is not { } person)
                    return Results.NotFound();

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    SessionClaims.Create(person, adult.Subject, CookieAuthenticationDefaults.AuthenticationScheme),
                    new AuthenticationProperties { IsPersistent = true });
                await events.RecordAsync(SecurityEvent.Of(SecurityEventNames.Login, person.Id), Actor.Browser(person.Id, person.IsAdmin), http.RequestAborted);
                return Results.Redirect(target);
            })
            .ExcludeFromDescription();
        return routes;
    }
}

/// <summary>
/// Demo mode only: what would reach outside the demo, or let one visitor spoil it for the others, is refused, whoever asks.
/// Access tokens (an assistant could keep reading the instance), webhooks and channel tests (the server would post to an
/// address a visitor typed), model workers and cloud provider keys (the server would call an address or spend a key a
/// visitor gave). Document uploads (the demo would host a visitor's files under its domain), imports (a visitor could fill
/// the database), signing out everywhere (every visitor shares the same adults: all of them would be signed out), the
/// admin role and the household settings (the next visitor would find the demo changed under them).
/// </summary>
public sealed class DemoGuardMiddleware(RequestDelegate next, DemoOptions options)
{
    /// <summary>The header that carries <see cref="DemoOptions.SeedKey"/>: the nightly reset (<c>finish.mjs</c>) sends it.</summary>
    public const string SeedKeyHeader = "X-Nid-Demo-Seed";

    /// <summary>The <c>code</c> of the refusal, which the web app turns into its own sentence.</summary>
    public const string RefusedCode = "demo.unavailable";

    private static readonly (string Method, string Path)[] Refused =
    [
        ("POST", "/api/v1/core/me/tokens"),
        ("PUT", "/api/v1/platform/notifications/discord"),
        ("POST", "/api/v1/platform/notifications/test"),
        ("PUT", "/api/v1/admin/notifications/discord"),
        ("POST", "/api/v1/admin/notifications/test"),
        ("POST", "/api/v1/ai/workers"),
        ("POST", "/api/v1/ai/workers/*/pair"),
        ("PUT", "/api/v1/ai/workers/*/safe"),
        ("PUT", "/api/v1/ai/cloud/keys/*"),
        ("DELETE", "/api/v1/ai/cloud/keys/*"),
        ("POST", "/api/v1/platform/imports"),
        ("POST", "/api/v1/core/me/revoke-sessions"),
        ("POST", "/api/v1/admin/users/*/revoke-sessions"),
        ("PUT", "/api/v1/admin/users/*/admin"),
    ];

    /// <summary>Refused too, except to the nightly reset, which adds the demo's documents and turns the CPI on with them.</summary>
    private static readonly (string Method, string Path)[] SeedOnly =
    [
        ("POST", "/api/v1/documents"),
        ("PATCH", "/api/v1/admin/household"),
    ];

    /// <summary>A <c>*</c> segment of a refused path stands for any one segment (a worker's id, a provider).</summary>
    public static bool IsRefused(string method, string path) => Matches(Refused, method, path);

    /// <summary>Refused unless the request carries the seed key (<see cref="SeedKeyHeader"/>).</summary>
    public static bool IsSeedOnly(string method, string path) => Matches(SeedOnly, method, path);

    private static bool Matches((string Method, string Path)[] routes, string method, string path)
    {
        var segments = path.TrimEnd('/').Split('/');
        return routes.Any(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
                               && r.Path.Split('/') is var pattern && pattern.Length == segments.Length
                               && pattern.Zip(segments).All(p => p.First == "*" || string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>No key configured opens nothing; compared in constant time.</summary>
    private bool HasSeedKey(HttpRequest request)
        => !string.IsNullOrEmpty(options.SeedKey) && request.Headers[SeedKeyHeader].ToString() is { Length: > 0 } sent
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(options.SeedKey));

    public async Task InvokeAsync(HttpContext context)
    {
        var (method, path) = (context.Request.Method, context.Request.Path.Value ?? "");
        if (IsRefused(method, path) || (IsSeedOnly(method, path) && !HasSeedKey(context.Request)))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not available in the demo.",
                detail: "The demo does not create access tokens, webhooks, test messages, model workers or cloud keys, takes no documents or imports, "
                        + "and keeps its sessions, admin role and household settings as they are.",
                extensions: new Dictionary<string, object?> { ["code"] = RefusedCode }).ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
