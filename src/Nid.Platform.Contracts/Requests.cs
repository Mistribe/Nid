using Nid.Core.Contracts;

namespace Nid.Platform.Contracts;

/// <summary>A request that declares its scope instead of the default (<c>read</c> for queries, <c>write:direct</c> for commands; the proposal submission asks <c>write</c>).</summary>
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

/// <summary>As <see cref="IItemScoped"/> when <see cref="ItemId"/> is set; a proposal to create an item has none to check.</summary>
public interface IOptionalItemScoped
{
    ItemId? ItemId { get; }

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

/// <summary>
/// Reserved to administrators (authorization.md §4, stage 2 bis of the pipeline). Touches no item: the admin
/// area shows no label, no amount and no item count.
/// </summary>
public interface IAdminOnly;

public static class AdminCodes
{
    /// <summary>Written <c>admin_required</c> in the documentation; HTTP 403.</summary>
    public const string Required = "platform.admin_required";
}

/// <summary>
/// Only a browser session may run it (spec 4A §4.2): managing tokens, « Déconnecter partout », the import. A token is
/// refused at the scope stage with <see cref="BrowserOnlyCodes.Required"/>, whatever its scopes.
/// </summary>
public interface IBrowserOnly;

public static class BrowserOnlyCodes
{
    /// <summary>HTTP 403.</summary>
    public const string Required = "platform.browser_only";
}
