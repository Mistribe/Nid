using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Contracts;

/// <summary>Marks requests that change state. Pipelines use it to require the <c>write</c> scope.</summary>
public interface ICommandMarker;

public interface ICommand<TResponse> : IRequest<TResponse>, ICommandMarker
    where TResponse : notnull;

public interface IQuery<TResponse> : IRequest<TResponse>
    where TResponse : notnull;

/// <summary>A request aimed at one item: the access behavior checks the actor against it.</summary>
public interface IItemScoped
{
    ItemId ItemId { get; }

    AccessKind Access { get; }
}
