using Nid.Core.Contracts;
using SynapseRequiresRole = UnambitiousFx.Synapse.Authorization.IRequiresRole;
using SynapseRequiresScope = UnambitiousFx.Synapse.Authorization.IRequiresScope;
using SynapseResourceScoped = UnambitiousFx.Synapse.Authorization.IResourceScoped;
using SynapseOptionallyResourceScoped = UnambitiousFx.Synapse.Authorization.IOptionallyResourceScoped;
using AuthorizationBehaviorOrder = UnambitiousFx.Synapse.Authorization.AuthorizationBehaviorOrder;

namespace Nid.Platform.Contracts;

/// <summary>
/// A request that declares its scope instead of the default (<c>read</c> for queries, <c>write:direct</c> for commands; the
/// proposal submission asks <c>write</c>). Synapse's scope check reads it through <see cref="SynapseRequiresScope"/>.
/// </summary>
public interface IRequiresScope : SynapseRequiresScope
{
    string RequiredScope { get; }

    string SynapseRequiresScope.Scope => RequiredScope;
}

/// <summary>Writes that bypass validation by a person, such as an import (<c>authorization.md</c> §3).</summary>
public interface IDirectWrite : IRequiresScope
{
    string IRequiresScope.RequiredScope => ScopeNames.WriteDirect;
}

/// <summary>A request aimed at one item: Synapse's resource check asks the item access policy about it.</summary>
public interface IItemScoped : SynapseResourceScoped
{
    ItemId ItemId { get; }

    new AccessKind Access { get; }

    string SynapseResourceScoped.ResourceId => ItemId.Value.ToString();

    string SynapseResourceScoped.Access => Access.ToWire();
}

/// <summary>As <see cref="IItemScoped"/> when <see cref="ItemId"/> is set; a proposal to create an item has none to check.</summary>
public interface IOptionalItemScoped : SynapseOptionallyResourceScoped
{
    ItemId? ItemId { get; }

    new AccessKind Access { get; }

    string? SynapseOptionallyResourceScoped.OptionalResourceId => ItemId?.Value.ToString();

    string SynapseResourceScoped.Access => Access.ToWire();
}

/// <summary>
/// #516: the position of the behaviors a module declares for its own resources (<c>PipelineOrder.ResourceAccess</c>), here because
/// modules see Platform's contracts, not Platform: after Synapse's item checks, before the handler.
/// </summary>
public static class ModuleBehaviorOrder
{
    public const uint ResourceAccess = AuthorizationBehaviorOrder.Resource + 600;
}

public static class AccessKindWire
{
    /// <summary>The access a denial is recorded with (<c>read</c>, <c>write</c>, <c>own</c>; spec U12).</summary>
    public static string ToWire(this AccessKind access) => access.ToString().ToLowerInvariant();
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
/// Reserved to administrators (authorization.md §4, stage 2 bis of the pipeline): Synapse's role check asks for
/// <see cref="Roles.Admin"/>. Touches no item: the admin area shows no label, no amount and no item count.
/// </summary>
public interface IAdminOnly : SynapseRequiresRole
{
    string SynapseRequiresRole.Role => Roles.Admin;
}

/// <summary>The roles an actor holds; the one there is is <see cref="Actor.IsAdmin"/>.</summary>
public static class Roles
{
    public const string Admin = "admin";
}

public static class AdminCodes
{
    /// <summary>Written <c>admin_required</c> in the documentation; HTTP 403.</summary>
    public const string Required = "platform.admin_required";
}

/// <summary>
/// Only a browser session may run it (spec 4A §4.2): managing tokens, « Déconnecter partout », the import. A token is
/// refused just before the scope check with <see cref="BrowserOnlyCodes.Required"/>, whatever its scopes.
/// </summary>
public interface IBrowserOnly;

/// <summary>
/// Spec #23: never run by a token for real — only as a proposal's dry run (<see cref="ScopeNames.DryRun"/>); a browser runs it,
/// and so does the replay of an approved proposal (the approving adult's browser session).
/// </summary>
public interface IConfirmedInBrowser;

public static class BrowserOnlyCodes
{
    /// <summary>HTTP 403.</summary>
    public const string Required = "platform.browser_only";
}
