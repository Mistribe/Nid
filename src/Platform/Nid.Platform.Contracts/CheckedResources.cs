using System.Runtime.CompilerServices;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Contracts;

/// <summary>
/// #516: what an access behavior loaded to check a request (a bank account, a document, a proposal), handed to the request's handler
/// through the request context, so the handler does not read it a second time. Keyed by the request instance: a request a handler sends
/// on (an approval's replay, a filing round's edits) never reads what was checked for its caller.
/// </summary>
public sealed class CheckedResources : IContextFeature
{
    private readonly ConditionalWeakTable<object, object> byRequest = [];

    public string Name => "nid.checked_resources";

    public static void Hand(IContextAccessor context, object request, object resource)
    {
        if (!context.Context.TryGetFeature<CheckedResources>(out var feature) || feature is null)
            context.Context.SetFeature(feature = new CheckedResources());
        feature.byRequest.AddOrUpdate(request, resource);
    }

    /// <summary>What the behavior checked for <paramref name="request"/>; a handler reached without it is a wiring fault, not a refusal.</summary>
    public static T Of<T>(IContextAccessor context, object request)
        => context.Context.TryGetFeature<CheckedResources>(out var feature) && feature is not null
           && feature.byRequest.TryGetValue(request, out var resource) && resource is T found
            ? found
            : throw new InvalidOperationException(
                $"No checked {typeof(T).Name} for {request.GetType().Name}: is its access behavior declared by the host?");
}
