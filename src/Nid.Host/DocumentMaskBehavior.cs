using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Platform;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host;

/// <summary>
/// #108: a response leaves the pipeline (REST and MCP alike) with a document id only for a reader of that document; one batched check per
/// response. Here, not in Platform, because Core does not know Documents.
/// </summary>
public sealed partial class DocumentMaskBehavior<TRequest, TResponse>(IInvoker invoker, ILogger<DocumentMaskBehavior<TRequest, TResponse>> logger)
    : IRequestPipelineBehavior<TRequest, TResponse>, IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
    where TResponse : notnull
{
    public uint Order => PipelineOrder.DocumentMask;

    public async ValueTask<Result<TResponse>> HandleAsync(
        TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken = default)
    {
        var result = await next(request, cancellationToken);
        if (!result.TryGetValue(out var response) || response is not IExposesDocuments exposing)
            return result;

        var exposed = exposing.ExposedDocuments().ToHashSet();
        if (exposed.Count == 0)
            return result;

        // A failed or thrown check hides them all: a source is a pointer, never a reason to withhold the figures.
        IReadOnlySet<Guid> readable = new HashSet<Guid>();
        try
        {
            if ((await invoker.InvokeAsync(new FilterReadableDocumentsQuery(exposed), cancellationToken)).TryGetValue(out var ids))
                readable = ids!;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogCheckFailed(e, typeof(TRequest).Name);
        }

        // The leak bench checks that every IExposesDocuments response masks into its own type, so this cast holds.
        return Result.Success((TResponse)exposing.WithReadableDocuments(readable));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The readable documents of {Request} could not be checked; every document id is hidden.")]
    private partial void LogCheckFailed(Exception exception, string request);
}
