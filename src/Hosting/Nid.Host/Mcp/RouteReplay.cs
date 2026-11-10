using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Nid.Host.Auth;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Mcp;

/// <summary>A route's arguments, as a proposal stores them: route values, query values and the JSON body.</summary>
public sealed record RouteCall(IReadOnlyDictionary<string, string> Route, IReadOnlyDictionary<string, string> Query, JsonElement? Body);

/// <summary>
/// Spec 4D3: runs a REST route in-process, as the user of the request being served (the assistant's token, or the adult
/// approving). Its endpoint filters run (the actor is set as REST sets it), the middleware does not: authentication
/// already ran on the outer request. The answer comes back as a <see cref="Result{T}"/>: the route's JSON on success, its
/// Problem Details as a failure whose fields are the proposal's (`payload.body.owners[0].shareBp`).
/// </summary>
public sealed class RouteReplay(EndpointDataSource endpoints, IHttpContextAccessor http, IServiceScopeFactory scopes, IActorAccessor actor)
{
    public RouteEndpoint? Find(RouteAction action)
        => endpoints.Endpoints.OfType<RouteEndpoint>().FirstOrDefault(e => e.RoutePattern.RawText == action.Route
            && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(action.Method) == true);

    /// <summary>Applies the route in <paramref name="services"/>' scope: inside the caller's unit of work when one is open.</summary>
    public Task<Result<JsonElement>> RunAsync(IServiceProvider services, RouteAction action, RouteCall call, CancellationToken ct)
        => SendAsync(services, http.HttpContext?.User, action, call, ct);

    /// <summary>
    /// Runs the route in a scope of its own, inside a unit of work that is never committed: what it would refuse, without
    /// writing. A token proposes with `write` only, so the dry run lends it `write:direct`: nothing it writes lasts. A job runs it as its
    /// own actor, lent `write:direct` the same way: the bank sync proposing as a person (D22), even inside another person's request (a
    /// manual sync, a statement import).
    /// </summary>
    public async Task<Result<JsonElement>> DryRunAsync(RouteAction action, RouteCall call, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var rolledBack = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var user = actor.Current?.Via == ActorOrigin.Job ? null : http.HttpContext?.User;
        if (user is null && actor.Current is { } job)
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = job with { Scopes = new HashSet<string>(job.Scopes) { ScopeNames.WriteDirect, ScopeNames.DryRun } };
        return await SendAsync(scope.ServiceProvider, user is null ? null : TokenClaims.WithScope(TokenClaims.WithScope(user, ScopeNames.WriteDirect), ScopeNames.DryRun), action, call, ct);
    }

    private async Task<Result<JsonElement>> SendAsync(
        IServiceProvider services, ClaimsPrincipal? user, RouteAction action, RouteCall call, CancellationToken ct)
    {
        if (Find(action)?.RequestDelegate is not { } run)
            throw new InvalidOperationException($"No endpoint for {action.Method} {action.Route}.");

        var context = new DefaultHttpContext { RequestServices = services, User = user ?? new ClaimsPrincipal(), RequestAborted = ct };
        context.Request.Method = action.Method;
        // The ActorFilter tells an assistant's call by the outer request's path (`/mcp`).
        context.Request.Path = http.HttpContext?.Request.Path ?? PathString.Empty;
        foreach (var (name, value) in call.Route)
            context.Request.RouteValues[name] = value;
        context.Request.QueryString = QueryString.Create(call.Query.Select(q => KeyValuePair.Create(q.Key, (string?)q.Value)));
        if (call.Body is { } body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.GetRawText());
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            // Minimal APIs read a body only when the server says the request may have one.
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        }
        using var response = new MemoryStream();
        context.Response.Body = response;

        await run(context);

        // No content (a 204) is an undefined element.
        var json = response.Length == 0 ? default : JsonDocument.Parse(response.ToArray()).RootElement.Clone();
        return context.Response.StatusCode is >= 200 and < 300
            ? Result.Success(json)
            : Result.Failure<JsonElement>(Refusal(context.Response.StatusCode, json, action));
    }

    /// <summary>The route's Problem Details as a failure: 404 stays not found; any other refusal keeps its code, title and fields.</summary>
    private static Failure Refusal(int status, JsonElement problem, RouteAction action)
    {
        var code = problem is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("code", out var c) ? c.GetString() : null;
        var title = problem is { ValueKind: JsonValueKind.Object } t && t.TryGetProperty("title", out var m) ? m.GetString() ?? "" : "";
        if (status == StatusCodes.Status404NotFound)
            return new NotFoundFailure(action.Kind, "");
        code ??= status == StatusCodes.Status400BadRequest ? "request.malformed" : "request.invalid";
        if (problem is { ValueKind: JsonValueKind.Object } withErrors && withErrors.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array)
            return FieldErrors.Many(code, title, [.. errors.EnumerateArray().Select(e => new FieldError(
                PathOf(action, e.TryGetProperty("path", out var path) ? path.GetString() ?? "" : ""),
                e.TryGetProperty("code", out var fieldCode) ? fieldCode.GetString() ?? code : code,
                e.TryGetProperty("message", out var message) ? message.GetString() ?? "" : ""))]);
        return new Failure(code, title.Length > 0 ? title : $"Refused ({status}).", null);
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    /// <summary>A route or query parameter is the payload's own field; anything else is in the body.</summary>
    private static string PathOf(RouteAction action, string path)
        => action.RouteParameters.Contains(path) || (action.Query?.Contains(path) ?? false)
            ? $"payload.{path}"
            : path.Length == 0 ? "payload.body" : $"payload.body.{path}";
}
