using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Host.Auth;

/// <summary>Spec 4A §3: <c>Authorization: Bearer nid_pat_…</c> on /api (and /mcp from 4B).</summary>
public static class TokenAuthentication
{
    public const string Scheme = "nid-pat";

    /// <summary>The default scheme: forwards to <see cref="Scheme"/> for a bearer token, to the cookie otherwise.</summary>
    public const string Policy = "nid";

    private const string BearerPrefix = "Bearer nid_pat_";

    public static bool IsBearer(HttpRequest request)
        => request.Headers.Authorization.ToString().StartsWith(BearerPrefix, StringComparison.Ordinal);

    public static string? TokenOf(HttpRequest request)
        => IsBearer(request) ? request.Headers.Authorization.ToString()["Bearer ".Length..] : null;

    public static string ClientAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>60 requests a minute per token (spec 4A §7); cookie requests are not limited here.</summary>
    /// <remarks>
    /// Built by the container, which disposes it with the provider, and applied by <see cref="TokenRateLimitMiddleware"/>
    /// rather than ASP.NET's rate limiting: neither a limiter set in its options nor the endpoint limiter its middleware
    /// builds is ever disposed, and their heartbeat timers kept every stopped host alive (each test host, ~7 MB).
    /// </remarks>
    public static IServiceCollection AddTokenRateLimiter(this IServiceCollection services)
        => services.AddSingleton(_ => PartitionedRateLimiter.Create<HttpContext, string>(context =>
            TokenClaims.Read(context.User) is { } token
                ? RateLimitPartition.GetFixedWindowLimiter(token.TokenId.ToString(), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
                })
                : RateLimitPartition.GetNoLimiter("")));
}

/// <summary>
/// A token principal's own claim types (plan precision 1): <see cref="SessionClaims"/> never reads them, so /auth/*
/// and the CSRF token see no session on a bearer request.
/// </summary>
public static class TokenClaims
{
    private const string Person = "nid:token_person";
    private const string Id = "nid:token_id";
    private const string Scope = "nid:token_scope";

    public static ClaimsPrincipal Create(TokenCredential token)
        => new(new ClaimsIdentity(
            new[] { new Claim(Person, token.Person.Value.ToString()), new Claim(Id, token.Id.ToString()) }
                .Concat(token.Scopes.Select(s => new Claim(Scope, s))),
            TokenAuthentication.Scheme));

    /// <summary>A token's principal with one more scope (a browser session's unchanged): only for a dry run that never commits.</summary>
    public static ClaimsPrincipal WithScope(ClaimsPrincipal user, string scope)
        => Read(user) is null
            ? user
            : new(new ClaimsIdentity(user.Claims.Append(new Claim(Scope, scope)), TokenAuthentication.Scheme));

    public static (PersonId Person, Guid TokenId, IReadOnlySet<string> Scopes)? Read(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(Person), out var person) && Guid.TryParse(user.FindFirstValue(Id), out var id)
            ? (new PersonId(person), id, user.FindAll(Scope).Select(c => c.Value).ToHashSet(StringComparer.Ordinal))
            : null;
}

public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IPersonalAccessTokens tokens, IPersonRegistry persons, ISecurityEvents events, TokenFailureGate gate, IClock clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = TokenAuthentication.TokenOf(Request);
        if (token is null)
            return AuthenticateResult.NoResult();

        var readable = TokenFormat.TryReadPrefix(token, out var prefix);
        var credential = readable ? await tokens.FindByPrefixAsync(prefix, Context.RequestAborted) : null;
        var person = credential is null ? null : await persons.FindAsync(credential.Person, Context.RequestAborted);
        if (credential is null || !TokenFormat.Matches(token, credential.Hash) || credential.RevokedAt is not null
            || credential.ExpiresAt <= clock.GetCurrentInstant()
            || person is null || person.Kind != PersonKind.Adult || person.IsPending)
            return await FailAsync(readable ? prefix : null, credential?.Person);

        await tokens.TouchAsync(credential.Id, Context.RequestAborted);
        return AuthenticateResult.Success(new AuthenticationTicket(TokenClaims.Create(credential), TokenAuthentication.Scheme));
    }

    /// <summary>Never says why: an unknown, revoked or expired token look the same from outside.</summary>
    private async Task<AuthenticateResult> FailAsync(string? prefix, PersonId? subject)
    {
        if (gate.Fail(TokenAuthentication.ClientAddress(Context)))
        {
            var data = new Dictionary<string, string>();
            if (prefix is not null) data["prefix"] = prefix;
            var via = Request.Path.StartsWithSegments("/mcp") ? ActorOrigin.Mcp : ActorOrigin.Api;
            await events.RecordAsync(new SecurityEvent(SecurityEventNames.TokenAuthFailed, subject, [], data), Actor.Unidentified(via), Context.RequestAborted);
        }
        return AuthenticateResult.Fail("Invalid token.");
    }
}

/// <summary>
/// Spec 4A §7 and plan precision 2: failed authentications per client address, in a window of 10 minutes. A bounded
/// map, as <see cref="Nid.Platform.Audit.AccessDeniedThrottle"/>: at worst a cleared map forgets some failures.
/// </summary>
public sealed class TokenFailureGate(IClock clock)
{
    private const int Limit = 20;
    private const int MaxRemembered = 10_000;
    private static readonly NodaTime.Duration Window = NodaTime.Duration.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (Instant Since, int Count, long LoggedMinute)> failures = new();

    public bool IsBlocked(string address)
        => failures.TryGetValue(address, out var f) && clock.GetCurrentInstant() - f.Since < Window && f.Count >= Limit;

    /// <summary>Counts one failure; <c>true</c> when it is the first of its minute for this address (one event a minute).</summary>
    public bool Fail(string address)
    {
        if (failures.Count > MaxRemembered)
            failures.Clear();
        var now = clock.GetCurrentInstant();
        var minute = now.ToUnixTimeSeconds() / 60;
        var record = true;
        failures.AddOrUpdate(address,
            _ => (now, 1, minute),
            (_, f) =>
            {
                var fresh = now - f.Since >= Window;
                record = fresh || f.LoggedMinute != minute;
                return fresh ? (now, 1, minute) : (f.Since, f.Count + 1, minute);
            });
        return record;
    }
}

/// <summary>Before authentication: an address past its failures is answered 429 without touching the database.</summary>
public sealed class TokenFailureGateMiddleware(RequestDelegate next, TokenFailureGate gate)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (TokenAuthentication.IsBearer(context.Request) && gate.IsBlocked(TokenAuthentication.ClientAddress(context)))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "600";
            return Task.CompletedTask;
        }
        return next(context);
    }
}

/// <summary>After authentication: a token past its 60 requests of the minute is answered 429 (spec 4A §7).</summary>
public sealed class TokenRateLimitMiddleware(RequestDelegate next, PartitionedRateLimiter<HttpContext> limiter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var lease = limiter.AttemptAcquire(context);
        if (!lease.IsAcquired)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            return;
        }
        await next(context);
    }
}
