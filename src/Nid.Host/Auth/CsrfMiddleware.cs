using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Nid.Host.Auth;

/// <summary>
/// Unsafe methods need a custom header carrying the session's CSRF token, and a same-origin
/// <c>Origin</c> / <c>Sec-Fetch-Site</c> when the browser sends them. No CORS is configured (single origin).
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next, IOptions<NidAuthOptions> options, IHostEnvironment environment)
{
    public const string HeaderName = "X-Nid-Csrf";

    private static readonly HashSet<string> SafeMethods = ["GET", "HEAD", "OPTIONS"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method) || !RequiresCsrf(context.Request.Path))
        {
            await next(context);
            return;
        }

        // The identity provider is not present in tests, so there is no session to bind a CSRF token to;
        // the actor header (IntegrationTest environment only, never set in production) stands in for it.
        if (environment.IsEnvironment("IntegrationTest") && context.Request.Headers.ContainsKey("X-Nid-Test-Actor"))
        {
            await next(context);
            return;
        }

        if (!IsSameOrigin(context.Request) || !HasValidToken(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool RequiresCsrf(PathString path)
        => path.StartsWithSegments("/api") || path.StartsWithSegments("/auth/logout");

    private bool IsSameOrigin(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin" && site != "none")
            return false;

        if (request.Headers.TryGetValue("Origin", out var origin)
            && !string.Equals(origin.ToString().TrimEnd('/'), options.Value.PublicUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private bool HasValidToken(HttpContext context)
    {
        var person = SessionClaims.ReadPersonId(context.User);
        var epoch = SessionClaims.ReadEpoch(context.User);
        if (person is null || epoch is null || !context.Request.Headers.TryGetValue(HeaderName, out var supplied))
            return false;

        var expected = SessionClaims.CsrfToken(options.Value.CsrfKey, person.Value, epoch.Value);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied.ToString()), Encoding.UTF8.GetBytes(expected));
    }
}
