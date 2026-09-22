using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform;

/// <summary>Pipeline positions (low = outermost). Gaps leave room for later behaviors.</summary>
public static class PipelineOrder
{
    public const uint Trace = 10;
    public const uint Actor = 20;
    public const uint Scope = 30;
    public const uint ItemAccess = 40;
}

public sealed partial class TraceBehavior<TRequest, TResponse>(ILogger<TraceBehavior<TRequest, TResponse>> logger)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.Trace;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var result = await next(request, cancellationToken);
        if (result.TryGetFailure(out var failure))
            LogFailure(typeof(TRequest).Name, failure!.Code);
        return result;
    }

    // Never log request contents: they can hold names and amounts.
    [LoggerMessage(Level = LogLevel.Information, Message = "{Request} failed with {Code}")]
    private partial void LogFailure(string request, string code);
}

/// <summary>Requires an actor: no request runs anonymously.</summary>
public sealed class ActorBehavior<TRequest, TResponse>(IActorAccessor accessor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.Actor;

    public ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
        => accessor.Current is null
            ? ValueTask.FromResult(Result.Failure<TResponse>(new UnauthenticatedFailure("Authentication is required.", null)))
            : next(request, cancellationToken);
}

/// <summary>Reads need <c>read</c>, commands need <c>write</c>.</summary>
public sealed class ScopeBehavior<TRequest, TResponse>(IActorAccessor accessor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.Scope;

    public ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var required = request is ICommandMarker ? ScopeNames.Write : ScopeNames.Read;
        return accessor.Current!.Has(required)
            ? next(request, cancellationToken)
            : ValueTask.FromResult(Result.Failure<TResponse>(new UnauthorizedFailure($"The '{required}' scope is required.", null)));
    }
}

/// <summary>Checks the actor against the item a request targets.</summary>
public sealed class ItemAccessBehavior<TRequest, TResponse>(IActorAccessor accessor, IAccessPolicy policy)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.ItemAccess;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        if (request is not IItemScoped scoped)
            return await next(request, cancellationToken);

        var actor = accessor.Current!;
        if (actor.PersonId is not { } person)
            return await next(request, cancellationToken); // system actors are not bound to a person

        var check = await policy.CheckAsync(person, scoped.ItemId, scoped.Access, cancellationToken);
        return check.TryGetFailure(out var failure)
            ? Result.Failure<TResponse>(failure!)
            : await next(request, cancellationToken);
    }
}
