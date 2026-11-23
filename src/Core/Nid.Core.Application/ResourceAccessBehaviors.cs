using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>
/// #516, architecture.md §5.4: the proposal an <see cref="IProposalScoped"/> request names, checked through its target's rights here rather
/// than in each handler (<see cref="ProposalAccess.LoadAsync"/>, which records a refusal as before). The proposal goes to the handler
/// through the request context (<see cref="CheckedResources"/>).
/// </summary>
public sealed class ProposalAccessBehavior<TRequest, TResponse>(IServiceProvider services, IContextAccessor context)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => ModuleBehaviorOrder.ResourceAccess;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        if (request is not IProposalScoped scoped)
            return await next(request, cancellationToken);

        var loaded = await ProposalAccess.LoadAsync(services.GetRequiredService<IProposalStore>(), services.GetRequiredService<IAccessPolicy>(),
            services.GetRequiredService<IActorAccessor>(), services.GetRequiredService<IAccessDenials>(), scoped.ProposalId, scoped.Access,
            cancellationToken);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<TResponse>(loaded.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        CheckedResources.Hand(context, request, found.Proposal);
        return await next(request, cancellationToken);
    }
}

/// <summary>
/// #516, #294: an <see cref="IReplacesScenario"/> request's scenario, loaded as its handler did (an archived one or one of another kind is
/// « not found »), then a change of its visibility refused to whoever does not own it. The scenario and its item go to the handler
/// through the request context (<see cref="CheckedResources"/>).
/// </summary>
public sealed class ScenarioAccessBehavior<TRequest, TResponse>(IServiceProvider services, IContextAccessor context)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => ModuleBehaviorOrder.ResourceAccess;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        if (request is not IReplacesScenario replace)
            return await next(request, cancellationToken);

        var loaded = await ScenarioResults.LoadAsync(services.GetRequiredService<IItemRegistry>(), services.GetRequiredService<IScenarioStore>(),
            replace.ItemId, cancellationToken, replace.ScenarioKind);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<TResponse>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        if (replace.Visibility != found.Item.Visibility
            && !services.GetRequiredService<IItemRules>().CanCreate(services.GetRequiredService<IActorAccessor>().Current!.PersonId!.Value, found.Item.Owners))
            return Result.Failure<TResponse>(new UnauthorizedFailure("Only the scenario's owners change its visibility.", null));
        CheckedResources.Hand(context, request, new LoadedScenario(found.Item, found.Stored));
        return await next(request, cancellationToken);
    }
}

/// <summary>A scenario's item and stored document, as <see cref="ScenarioAccessBehavior{TRequest,TResponse}"/> hands them on.</summary>
internal sealed record LoadedScenario(ItemAccess Item, StoredScenario Stored);
