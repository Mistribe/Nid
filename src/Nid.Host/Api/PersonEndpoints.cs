using System.Text.Json.Serialization;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using Nid.Income.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using IResult = Microsoft.AspNetCore.Http.IResult;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

public static class PersonEndpoints
{
    public sealed record PersonDto(Guid Id, string Kind, string DisplayName, string? Email);

    public sealed record CreateDependentBody(string DisplayName);

    /// <summary>A key nobody reads is refused rather than silently dropped (400, like any unreadable body).</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record PreferencesBody(string? Theme, string? Mode, bool? ChartPatterns, Dictionary<string, int>? GuidesSeen);

    public static IEndpointRouteBuilder MapPersonEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/core").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/persons", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListPersonsQuery(), ct)).ToHttp(list => list.Select(ToDto)))
            .Returns<IEnumerable<PersonDto>>();

        api.MapPost("/persons/dependents", async (CreateDependentBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CreateDependentCommand(body.DisplayName), ct)).ToHttp(ToDto, StatusCodes.Status201Created))
            .Returns<PersonDto>(StatusCodes.Status201Created);

        api.MapPatch("/me/preferences", async (PreferencesBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new UpdatePreferencesCommand(new PreferencesChange(body.Theme, body.Mode, body.ChartPatterns, body.GuidesSeen)), ct))
                .ToHttp(p => p))
            .Returns<Preferences>();

        // "Déconnecter partout" for oneself (administration-and-audit.md §5, 1F3a spec U16); 4A: `revokeTokens` revokes
        // the tokens too. The success branch is written explicitly: ToHttp's Results.Json(null, statusCode: 204) would
        // still write a JSON body, which a 204 must not carry.
        api.MapPost("/me/revoke-sessions", async (RevokeSessionsBody? body, IInvoker invoker, CancellationToken ct) =>
        {
            var result = await invoker.InvokeAsync(new RevokeMySessionsCommand(body?.RevokeTokens ?? false), ct);
            return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
        }).ReturnsNoContent();

        // Issue 85: the Foyer / Moi selector only shows once « Moi » can differ from « Foyer ».
        api.MapGet("/me/private-items", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetMyPrivateItemsQuery(), ct)).ToHttp(v => v))
            .Returns<PrivateItemsView>();

        // Spec 4A §6: « Mes jetons », browser sessions only (IBrowserOnly).
        api.MapGet("/me/tokens", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListMyTokensQuery(), ct)).ToHttp(t => t))
            .Returns<IReadOnlyList<TokenView>>();

        api.MapPost("/me/tokens", async (NewTokenBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new CreateTokenCommand(body.Name, body.Preset, body.ExpiresInDays), ct))
                    .ToHttp(t => t, StatusCodes.Status201Created))
            .Returns<CreatedTokenView>(StatusCodes.Status201Created);

        api.MapDelete("/me/tokens/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct) =>
        {
            var result = await invoker.InvokeAsync(new RevokeMyTokenCommand(id), ct);
            return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
        }).ReturnsNoContent();

        return routes;
    }

    internal static PersonDto ToDto(PersonInfo p) => new(p.Id.Value, p.Kind.ToString().ToLowerInvariant(), p.DisplayName, p.Email);
}

/// <summary>
/// Puts the signed-in person in the scoped accessor so the pipeline can read it; handlers never read
/// <c>HttpContext</c>. In tests, <c>IntegrationTestAuthenticationHandler</c> already puts the same
/// <c>SessionClaims</c> a real session would carry onto <c>HttpContext.User</c>, so this filter needs no
/// test-specific branch. The admin flag is read from the person row on every request, never from the cookie:
/// a demotion takes effect at the next request (administration-and-audit.md §2.1).
/// </summary>
public sealed class ActorFilter(IActorAccessor accessor, IPersonRegistry persons) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (TokenClaims.Read(http.User) is { } token)
        {
            // The handler already checked the token and its person (spec 4A §3); a token is never admin.
            var via = http.Request.Path.StartsWithSegments("/mcp") ? ActorOrigin.Mcp : ActorOrigin.Api;
            accessor.Current = Actor.Token(token.Person, token.TokenId, token.Scopes, via);
        }
        else if (SessionClaims.ReadPersonId(http.User) is { } id && await persons.FindAsync(id, http.RequestAborted) is { } person)
            accessor.Current = Actor.Browser(person.Id, person.IsAdmin);

        return await next(context);
    }
}

/// <summary>
/// Turns the <see cref="BadHttpRequestException"/> a request body throws on a value nobody can read into the
/// same Problem Details every other failure answers with. Kestrel already answers 400 to one that escapes,
/// but with no body saying which value was refused — and nothing an in-process test can observe.
/// </summary>
public sealed class MalformedInputFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BadHttpRequestException malformed)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: malformed.Message,
                extensions: new Dictionary<string, object?> { ["code"] = "request.malformed" });
        }
    }
}

public static class ResultHttp
{
    /// <summary>The single place where a <c>Result</c> becomes an HTTP response (Problem Details on failure).</summary>
    public static IResult ToHttp<T, TOut>(this Result<T> result, Func<T, TOut> map, int successStatus = StatusCodes.Status200OK)
        where T : notnull
        => result.TryGetValue(out var value)
            ? Results.Json(map(value), statusCode: successStatus)
            : ProblemFor(result.TryGetFailure(out var failure) ? failure! : new Failure("ERROR", "Unknown failure.", null));

    private static IResult ProblemFor(Failure failure)
    {
        var status = failure switch
        {
            NotFoundFailure => StatusCodes.Status404NotFound,
            UnauthorizedFailure => StatusCodes.Status403Forbidden,
            UnauthenticatedFailure => StatusCodes.Status401Unauthorized,
            _ when failure.Code == AdminCodes.Required => StatusCodes.Status403Forbidden,
            _ when failure.Code == BrowserOnlyCodes.Required => StatusCodes.Status403Forbidden,
            _ when failure.Code == TokenCodes.LimitReached => StatusCodes.Status409Conflict,
            _ when failure.Code == "ERROR" => StatusCodes.Status500InternalServerError,
            _ when failure.Code == "core.access_violation" => StatusCodes.Status500InternalServerError,
            _ when failure.Code == FxCodes.SyncRunning => StatusCodes.Status409Conflict,
            _ when failure.Code == CatalogCodes.SyncRunning => StatusCodes.Status409Conflict,
            _ when failure.Code is PriceCodes.SyncRunning or PriceCodes.Disabled or PriceCodes.NotRejected => StatusCodes.Status409Conflict,
            _ when failure.Code is IrlCodes.SyncRunning or IrlCodes.Disabled => StatusCodes.Status409Conflict,
            _ when failure.Code is FxCodes.InvalidRate or FxCodes.InvalidRange => StatusCodes.Status400BadRequest,
            _ when failure.Code == ExportCodes.RevisionChanged => StatusCodes.Status409Conflict,
            _ when failure.Code == ExportCodes.BadCursor => StatusCodes.Status400BadRequest,
            _ when ImportCodes.Conflicts.Contains(failure.Code) => StatusCodes.Status409Conflict,
            _ when ImportCodes.BadRequests.Contains(failure.Code) => StatusCodes.Status400BadRequest,
            _ when failure.Code == FieldErrors.ShapeCode => StatusCodes.Status422UnprocessableEntity,
            _ when failure.Code is CoreFactCodes.FactNotFound => StatusCodes.Status404NotFound,
            _ when failure.Code is CoreFactCodes.FactRetired or CoreFactCodes.OpeningExists or CoreFactCodes.ExternalRefTaken
                or CoreFactCodes.FactConflict => StatusCodes.Status409Conflict,
            _ when failure.Code is InvestmentsCodes.EnvelopeClosed or InvestmentsCodes.EnvelopeOpen => StatusCodes.Status409Conflict,
            _ when failure.Code is InvestmentsCodes.HoldingInUse or InvestmentsCodes.HoldingConflict or InvestmentsCodes.HoldingClosed
                or InvestmentsCodes.SnapshotExists => StatusCodes.Status409Conflict,
            _ when failure.Code == InvestmentsCodes.InvalidPeriod => StatusCodes.Status400BadRequest,
            _ when failure.Code is IncomeCodes.SourceEnded or IncomeCodes.SourceOpen or IncomeCodes.PhaseConflict => StatusCodes.Status409Conflict,
            _ when failure.Code == IncomeCodes.InvalidPeriod => StatusCodes.Status400BadRequest,
            _ when failure.Code is LoanCodes.LoanConflict or LoanCodes.FeeConflict or LoanCodes.RepaymentConflict
                or PropertyCodes.PropertyConflict or PropertyCodes.RecurringCostConflict or PropertyCodes.TenancyConflict => StatusCodes.Status409Conflict,
            _ when failure.Code is ProposalCodes.Conflict or ProposalCodes.Decided => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        var extensions = new Dictionary<string, object?> { ["code"] = failure.Code };
        if (FieldErrors.Of(failure) is { } errors)
            extensions["errors"] = errors.Select(e => new { path = e.Path, code = e.Code, message = e.Message }).ToList();
        // 2C2a spec §6: a rule that names its field in metadata (Investments', Income's) reaches the client the same way.
        else if (failure.Metadata is { } metadata && metadata.TryGetValue("field", out var field) && field is string path)
            extensions["errors"] = new[] { new { path, code = failure.Code, message = failure.Message } };
        return Results.Problem(statusCode: status, title: failure.Message, extensions: extensions);
    }
}

public sealed record RevokeSessionsBody(bool RevokeTokens);

public sealed record NewTokenBody(string Name, string Preset, int? ExpiresInDays);
