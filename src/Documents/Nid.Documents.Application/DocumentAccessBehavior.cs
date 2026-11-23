using Microsoft.Extensions.DependencyInjection;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.Application;

/// <summary>
/// #516, architecture.md §5.4: the document an <see cref="IReadsDocument"/> or <see cref="IEditsDocument"/> request names, checked here
/// rather than in each handler (<see cref="DocumentAccess.ReadAsync"/>, <see cref="DocumentAccess.EditableAsync"/>), with the failures
/// they answered. The <see cref="ReadableDocument"/> goes to the handler through the request context (<see cref="CheckedResources"/>).
/// </summary>
public sealed class DocumentAccessBehavior<TRequest, TResponse>(IServiceProvider services, IContextAccessor context, IActorAccessor actor)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => ModuleBehaviorOrder.ResourceAccess;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        if (request is not IReadsDocument scoped)
            return await next(request, cancellationToken);

        var access = services.GetRequiredService<DocumentAccess>();
        var person = actor.Current!.PersonId!.Value;
        var found = request is IEditsDocument
            ? await access.EditableAsync(scoped.DocumentId, person, cancellationToken)
            : await access.ReadAsync(scoped.DocumentId, person, cancellationToken);
        if (!found.TryGetValue(out var document))
            return Result.Failure<TResponse>(found.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        CheckedResources.Hand(context, request, document!);
        return await next(request, cancellationToken);
    }
}
