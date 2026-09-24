using System.Text.Json.Serialization;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using Nid.Platform.Contracts;
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
    public sealed record PreferencesBody(string? Theme, string? Mode, bool? ChartPatterns);

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
            => (await invoker.InvokeAsync(new UpdatePreferencesCommand(new PreferencesChange(body.Theme, body.Mode, body.ChartPatterns)), ct))
                .ToHttp(p => p))
            .Returns<Preferences>();

        return routes;
    }

    private static PersonDto ToDto(PersonInfo p) => new(p.Id.Value, p.Kind.ToString().ToLowerInvariant(), p.DisplayName, p.Email);
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
        if (SessionClaims.ReadPersonId(http.User) is { } id && await persons.FindAsync(id, http.RequestAborted) is { } person)
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
            _ when failure.Code == "ERROR" => StatusCodes.Status500InternalServerError,
            _ when failure.Code == "core.access_violation" => StatusCodes.Status500InternalServerError,
            _ when failure.Code == FxCodes.SyncRunning => StatusCodes.Status409Conflict,
            _ when failure.Code is FxCodes.InvalidRate or FxCodes.InvalidRange => StatusCodes.Status400BadRequest,
            _ when ImportCodes.Conflicts.Contains(failure.Code) => StatusCodes.Status409Conflict,
            _ when ImportCodes.BadRequests.Contains(failure.Code) => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return Results.Problem(statusCode: status, title: failure.Message, extensions: new Dictionary<string, object?> { ["code"] = failure.Code });
    }
}
