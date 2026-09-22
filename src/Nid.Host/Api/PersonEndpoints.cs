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

    public static IEndpointRouteBuilder MapPersonEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/core").RequireAuthorization().AddEndpointFilter<ActorFilter>();

        api.MapGet("/persons", async (IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListPersonsQuery(), ct)).ToHttp(list => list.Select(ToDto)));

        api.MapPost("/persons/dependents", async (CreateDependentBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CreateDependentCommand(body.DisplayName), ct)).ToHttp(ToDto, StatusCodes.Status201Created));

        return routes;
    }

    private static PersonDto ToDto(PersonInfo p) => new(p.Id.Value, p.Kind.ToString().ToLowerInvariant(), p.DisplayName, p.Email);
}

/// <summary>Puts the signed-in person in the scoped accessor so the pipeline can read it; handlers never read <c>HttpContext</c>.</summary>
public sealed class ActorFilter(IActorAccessor accessor) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (SessionClaims.ReadPersonId(context.HttpContext.User) is { } person)
            accessor.Current = Actor.Browser(person);
        return next(context);
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
            _ when failure.Code == "ERROR" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return Results.Problem(statusCode: status, title: failure.Message, extensions: new Dictionary<string, object?> { ["code"] = failure.Code });
    }
}
