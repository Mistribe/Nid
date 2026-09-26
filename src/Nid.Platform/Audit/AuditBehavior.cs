using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Audit;

/// <summary>The request the current scope runs, for the log (spec §4.1). The outermost request names it.</summary>
public sealed class AuditRequest
{
    public string? Name { get; set; }
}

/// <summary>Pipeline stage "Audit", right after the actor is known (1F3a spec §4.1).</summary>
public sealed class AuditBehavior<TRequest, TResponse>(AuditRequest audit)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.Audit;

    public ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        audit.Name ??= typeof(TRequest).Name;
        return next(request, cancellationToken);
    }
}
