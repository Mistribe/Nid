using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Contracts;

/// <summary>Marks requests that change state. Pipelines use it to require the <c>write</c> scope.</summary>
public interface ICommandMarker;

public interface ICommand<TResponse> : IRequest<TResponse>, ICommandMarker
    where TResponse : notnull;

public interface IQuery<TResponse> : IRequest<TResponse>
    where TResponse : notnull;

/// <summary>A request that needs more than the default scope (<c>read</c> for queries, <c>write</c> for commands).</summary>
public interface IRequiresScope
{
    string RequiredScope { get; }
}

/// <summary>Writes that bypass validation by a person, such as an import (<c>authorization.md</c> §3).</summary>
public interface IDirectWrite : IRequiresScope
{
    string IRequiresScope.RequiredScope => ScopeNames.WriteDirect;
}

/// <summary>A request aimed at one item: the access behavior checks the actor against it.</summary>
public interface IItemScoped
{
    ItemId ItemId { get; }

    AccessKind Access { get; }
}

/// <summary>
/// A request that brings an item into being (<c>authorization.md</c> §4). There is no item yet to check the
/// actor against, so the access behavior checks the owners it names instead: §5 lets an adult create for
/// themselves or for a dependent, never in the other adult's name alone.
/// </summary>
public interface ICreatesItem
{
    IReadOnlyList<OwnerShare> Owners { get; }

    Visibility Visibility { get; }
}
