using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using UnambitiousFx.Synapse.Authorization;

namespace Nid.Platform;

/// <summary>
/// Pipeline positions (low = outermost). Synapse's authorization checks run in their own stage, at
/// <see cref="AuthorizationBehaviorOrder"/>: actor, role (<see cref="IAdminOnly"/>), scope, request policy, resource.
/// Nid's own behaviors sit in the gaps between them, so the order of the stages of <c>authorization.md</c> §2 is kept.
/// </summary>
public static class PipelineOrder
{
    /// <summary>Observability stage: wraps every check, so a refusal is logged too.</summary>
    public const uint Trace = 10;

    /// <summary>Names the request for the audit log and records the security events commands declare (1F3a), once the actor is known.</summary>
    public const uint Audit = AuthorizationBehaviorOrder.Actor + 500;

    /// <summary>After the admin role, before the scope: a token is told the request is browser-only, whatever its scopes.</summary>
    public const uint BrowserOnly = AuthorizationBehaviorOrder.Scope - 500;

    public const uint ItemAccess = AuthorizationBehaviorOrder.Resource + 500;

    /// <summary>Nulls the document ids the actor cannot read (#108), on the response the output guard let through.</summary>
    public const uint DocumentMask = AuthorizationBehaviorOrder.Resource + 8_500;

    /// <summary>Innermost: it inspects exactly what the handler produced (architecture.md §166).</summary>
    public const uint OutputGuard = AuthorizationBehaviorOrder.Resource + 9_000;
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

/// <summary>
/// A token never runs an <see cref="IBrowserOnly"/> request, nor an <see cref="IConfirmedInBrowser"/> one outside a dry run
/// (spec 4A §4.2, spec #23). The scope rule itself (reads need <c>read</c>, commands <c>write:direct</c>) is Synapse's.
/// </summary>
public sealed class BrowserOnlyBehavior<TRequest, TResponse>(IActorAccessor accessor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.BrowserOnly;

    public ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var actor = accessor.Current!;
        return (request is IBrowserOnly || (request is IConfirmedInBrowser && !actor.Has(ScopeNames.DryRun))) && actor.TokenId is not null
            ? ValueTask.FromResult(Result.Failure<TResponse>(
                new Failure(BrowserOnlyCodes.Required, "This action is only available from a browser session.", null)))
            : next(request, cancellationToken);
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
