using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Tax.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>
/// #516, architecture.md §5.4: the notice an <see cref="ITaxAssessmentScoped"/> request names, checked here rather than in each handler
/// (<see cref="TaxAssessmentAccess.OwnAsync"/>: « not found » unless the actor declares it). The <see cref="StoredTaxAssessment"/> goes to
/// the handler through the request context (<see cref="CheckedResources"/>).
/// </summary>
public sealed class TaxAssessmentAccessBehavior<TRequest, TResponse>(IServiceProvider services, IContextAccessor context, IActorAccessor actor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => ModuleBehaviorOrder.TaxAssessments;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        if (request is not ITaxAssessmentScoped scoped)
            return await next(request, cancellationToken);

        if (await TaxAssessmentAccess.OwnAsync(services.GetRequiredService<ITaxAssessmentStore>(), actor, scoped.TaxAssessmentId, cancellationToken)
            is not { } own)
            return Result.Failure<TResponse>(TaxAssessmentAccess.NotFound(scoped.TaxAssessmentId));
        CheckedResources.Hand(context, request, own);
        return await next(request, cancellationToken);
    }
}
