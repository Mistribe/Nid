using Microsoft.Extensions.Options;
using Nid.Core.Application.Calendar;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Host.Auth;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary>Issue #80: « À venir » as an iCalendar feed a calendar app subscribes to, and Réglages › Calendrier.</summary>
public static class CalendarEndpoints
{
    /// <summary>The feed's route: no session and no header, the secret is the URL's last segment. Never traced (url.path holds it).</summary>
    public const string FeedPath = Microsoft.Extensions.Hosting.Extensions.CalendarFeedPath;

    public sealed record CalendarFeedBody(string? Scope, bool Amounts);

    /// <summary>The only answer carrying the feed's URL (its secret): shown once, as a token's.</summary>
    public sealed record CreatedCalendarFeedResponse(CalendarFeedView Feed, string Url);

    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder routes)
    {
        // A calendar app sends no header: the URL is the right. Unknown, forged, revoked and expired answer the same 404, and an adult
        // no longer active too. The feed's person reads with `read` only, through the same pipeline (and output guard) as any read.
        routes.MapGet(FeedPath + "/{secret}.ics", async (string secret, HttpContext http, IPersonalAccessTokens tokens, IPersonRegistry persons,
                IActorAccessor actor, IInvoker invoker, IOptions<NidAuthOptions> auth, IClock clock, CancellationToken ct) =>
            {
                var gone = Results.NotFound();
                if (!TokenFormat.TryReadCalendarPrefix(secret, out var prefix)
                    || await tokens.FindCalendarByPrefixAsync(prefix, ct) is not { } feed
                    || !TokenFormat.Matches(secret, feed.Hash) || feed.RevokedAt is not null || feed.ExpiresAt <= clock.GetCurrentInstant()
                    || await persons.FindAsync(feed.Person, ct) is not { Kind: PersonKind.Adult, IsPending: false })
                    return gone;

                await tokens.TouchAsync(feed.Id, ct);
                actor.Current = Actor.Token(feed.Person, feed.Id, new HashSet<string> { ScopeNames.Read }, ActorOrigin.Api);
                var read = await invoker.InvokeAsync(new GetCalendarFeedQuery(feed.Options), ct);
                if (!read.TryGetValue(out var events))
                    return read.ToHttp(v => v);
                var body = CalendarIcs.Write(events!.Events, feed.Options.Amounts, PublicUrl(auth, http.Request), clock.GetCurrentInstant());
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                return Results.Text(body, "text/calendar; charset=utf-8");
            })
            .AllowAnonymous()
            .Produces<string>(StatusCodes.Status200OK, "text/calendar")
            .Produces(StatusCodes.Status404NotFound);

        var api = routes.MapGroup("/api/v1/core/me/calendar").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetMyCalendarQuery(), ct)).ToHttp(v => v))
            .Returns<MyCalendarView>();

        // Creates the feed, or regenerates it: the old URL answers 404 from now on.
        api.MapPost("", async (CalendarFeedBody body, HttpRequest request, IOptions<NidAuthOptions> auth, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new CreateCalendarFeedCommand(body.Scope, body.Amounts), ct))
                    .ToHttp(created => new CreatedCalendarFeedResponse(created.Feed, $"{PublicUrl(auth, request)}{FeedPath}/{created.Secret}.ics"),
                        StatusCodes.Status201Created))
            .Returns<CreatedCalendarFeedResponse>(StatusCodes.Status201Created);

        api.MapPut("", async (CalendarFeedBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new UpdateCalendarFeedCommand(body.Scope, body.Amounts), ct)).ToHttp(v => v))
            .Returns<CalendarFeedView>();

        return routes;
    }

    /// <summary>Auth:PublicUrl, else the request's own origin (development, tests).</summary>
    private static string PublicUrl(IOptions<NidAuthOptions> auth, HttpRequest request)
        => auth.Value.PublicUrl is { Length: > 0 } url ? url.TrimEnd('/') : $"{request.Scheme}://{request.Host}";
}
