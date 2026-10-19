using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Audit;
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

    /// <summary>Names the request for the audit log and records the security events commands declare (1F3a).</summary>
    public const uint Audit = 22;

    public const uint Admin = 25;
    public const uint Scope = 30;
    public const uint ItemAccess = 40;

    /// <summary>Innermost: it inspects exactly what the handler produced (architecture.md §166).</summary>
    public const uint OutputGuard = 90;
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

/// <summary>Stage 2 bis of <c>authorization.md</c> §2: an <see cref="IAdminOnly"/> request needs an admin actor.</summary>
public sealed class AdminBehavior<TRequest, TResponse>(IActorAccessor accessor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.Admin;

    public ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
        => request is IAdminOnly && !accessor.Current!.IsAdmin
            ? ValueTask.FromResult(Result.Failure<TResponse>(
                new Failure(AdminCodes.Required, "This action is reserved to administrators.", null)))
            : next(request, cancellationToken);
}

/// <summary>Reads need <c>read</c>; commands need <c>write:direct</c> unless they declare less (<see cref="IRequiresScope"/> — the proposal submission asks <c>write</c>); a token never runs an <see cref="IBrowserOnly"/> request.</summary>
public sealed class ScopeBehavior<TRequest, TResponse>(IActorAccessor accessor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.Scope;

    public ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var actor = accessor.Current!;
        if (request is IBrowserOnly && actor.TokenId is not null)
            return ValueTask.FromResult(Result.Failure<TResponse>(
                new Failure(BrowserOnlyCodes.Required, "This action is only available from a browser session.", null)));

        var required = request is IRequiresScope declared
            ? declared.RequiredScope
            : request is ICommandMarker ? ScopeNames.WriteDirect : ScopeNames.Read;
        return actor.Has(required)
            ? next(request, cancellationToken)
            : ValueTask.FromResult(Result.Failure<TResponse>(new UnauthorizedFailure($"The '{required}' scope is required.", null)));
    }
}

/// <summary>
/// Stage 4 of <c>authorization.md</c> §2: checks the actor against the item a request targets
/// (<see cref="IItemScoped"/>) and, for a request that creates one (<see cref="ICreatesItem"/>), against the
/// owners it names — the one access rule of §5 that cannot be expressed as a check on an existing item.
/// </summary>
public sealed class ItemAccessBehavior<TRequest, TResponse>(
    IActorAccessor accessor, IAccessPolicy policy, IAccessDenials denials)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.ItemAccess;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var actor = accessor.Current!;
        if (actor.PersonId is not { } person)
            return await next(request, cancellationToken); // system actors are not bound to a person

        var target = request switch
        {
            IItemScoped scoped => (scoped.ItemId, scoped.Access),
            IOptionalItemScoped { ItemId: { } item } optional => (item, optional.Access),
            _ => ((ItemId, AccessKind)?)null,
        };
        if (target is var (itemId, access))
        {
            var check = await policy.CheckAsync(person, itemId, access, cancellationToken);
            if (check.TryGetFailure(out var failure))
            {
                await denials.RecordAsync(person, itemId, access.ToString().ToLowerInvariant(), cancellationToken);
                return Result.Failure<TResponse>(failure!);
            }
        }

        if (request is ICreatesItem creating)
        {
            var check = await policy.CheckCreationAsync(person, creating.Owners, cancellationToken);
            if (check.TryGetFailure(out var failure))
            {
                await denials.RecordAsync(person, null, "create", cancellationToken);
                return Result.Failure<TResponse>(failure!);
            }
        }

        return await next(request, cancellationToken);
    }
}
