using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Authorization;

namespace Nid.Platform;

/// <summary>The actor of the request, as Synapse's authorization checks read it (<c>authorization.md</c> §2, stages 1 to 3).</summary>
public sealed class ActorPrincipal(IActorAccessor accessor) : ICurrentPrincipal
{
    /// <summary>No request runs anonymously: an entry point that sets no actor is refused.</summary>
    public bool IsAuthenticated => accessor.Current is not null;

    public string? Id => accessor.Current?.PersonId?.Value.ToString();

    /// <summary><c>write:direct</c> includes <c>write</c>, which includes <c>read</c> (<see cref="Actor.Has"/>).</summary>
    public bool HasScope(string scope) => accessor.Current?.Has(scope) == true;

    public bool IsInRole(string role) => role == Roles.Admin && accessor.Current?.IsAdmin == true;

    /// <summary>
    /// The system and the CLI are bound to no person. They keep the actor, role and scope checks (they hold every scope and
    /// the admin role anyway) and skip only the resource checks: <see cref="ItemAccessPolicy"/> and <see cref="ItemCreationBehavior{TRequest,TResponse}"/>.
    /// </summary>
    public bool IsSystem => accessor.Current is { Kind: ActorKind.System };
}

public static class NidAuthorization
{
    /// <summary>
    /// Synapse's actor, role, scope and resource checks over <see cref="IActorAccessor"/>: reads need <c>read</c>, commands
    /// <c>write:direct</c> unless they declare less (<see cref="IRequiresScope"/>), <see cref="IAdminOnly"/> needs an admin,
    /// and an <see cref="IItemScoped"/> request needs the access it names on its item (<see cref="ItemAccessPolicy"/>).
    /// </summary>
    public static IServiceCollection AddNidAuthorization(this IServiceCollection services)
    {
        services.AddScoped<ICurrentPrincipal, ActorPrincipal>();
        return services.AddSynapseAuthorization(o =>
        {
            o.QueryScope = ScopeNames.Read;
            o.CommandScope = ScopeNames.WriteDirect;
            o.SystemBypass = SystemBypass.ResourceChecks;
            o.DenialFailure = DenialFailure;
            o.Policy<ItemAccessPolicy>();
            o.DenialRecorder<ItemAccessDenials>();
        });
    }

    /// <summary>The failures Nid answered before Synapse ran these checks: same types, codes and sentences.</summary>
    public static Failure? DenialFailure(AuthorizationDenial denial) => denial.Reason switch
    {
        AuthorizationDenialReason.Unauthenticated => new UnauthenticatedFailure("Authentication is required.", null),
        AuthorizationDenialReason.MissingRole => new UnauthorizedFailure("This action is reserved to administrators.", null)
            { Code = AdminCodes.Required },
        AuthorizationDenialReason.MissingScope => new UnauthorizedFailure($"The '{denial.Requirement}' scope is required.", null),
        AuthorizationDenialReason.ResourceHidden => new NotFoundFailure("item", ItemOf(denial).ToString()),
        AuthorizationDenialReason.ResourceAccessDenied => new UnauthorizedFailure(denial.Requirement == AccessKind.Own.ToWire()
            ? "Only the item's owners change its sharing, its visibility or its owners."
            : "You can read this item but not change it.", null),
        _ => null,
    };

    internal static ItemId ItemOf(AuthorizationDenial denial) => new(Guid.Parse(denial.ResourceId!));
}

/// <summary>
/// Stage 4 of <c>authorization.md</c> §2: the actor against the item an <see cref="IItemScoped"/> or <see cref="IOptionalItemScoped"/>
/// request targets. An item the actor cannot read is hidden (404, existence is not disclosed, as for a missing one); one they read
/// but may not change as asked is denied (403).
/// </summary>
public sealed class ItemAccessPolicy(IActorAccessor accessor, IAccessPolicy policy) : IResourceAccessPolicy
{
    public async ValueTask<bool> IsAllowedAsync(ICurrentPrincipal principal, IResourceScoped request, CancellationToken cancellationToken = default)
        => await CheckAsync(principal, request, cancellationToken) == ResourceAccess.Allowed;

    public async ValueTask<ResourceAccess> CheckAsync(ICurrentPrincipal principal, IResourceScoped request, CancellationToken cancellationToken = default)
    {
        var target = request switch
        {
            IItemScoped scoped => (scoped.ItemId, scoped.Access),
            IOptionalItemScoped { ItemId: { } item } optional => (item, optional.Access),
            _ => ((ItemId, AccessKind)?)null,
        };
        // Fails closed: a person-less actor that is not the system, or a resource that is not an item.
        if (accessor.Current?.PersonId is not { } person || target is not var (itemId, access))
            return ResourceAccess.Hidden;

        var check = await policy.CheckAsync(person, itemId, access, cancellationToken);
        return !check.TryGetFailure(out var failure) ? ResourceAccess.Allowed
            : failure is NotFoundFailure ? ResourceAccess.Hidden
            : ResourceAccess.Denied;
    }
}

/// <summary>Spec U12: an item access refused or hidden is recorded as an <c>access_denied</c>, with the access asked.</summary>
public sealed class ItemAccessDenials(IActorAccessor accessor, IAccessDenials denials) : IAuthorizationDenialRecorder
{
    public async ValueTask RecordAsync(AuthorizationDenial denial, CancellationToken cancellationToken = default)
    {
        if (denial.Reason is AuthorizationDenialReason.ResourceHidden or AuthorizationDenialReason.ResourceAccessDenied
            && accessor.Current?.PersonId is { } person)
            await denials.RecordAsync(person, NidAuthorization.ItemOf(denial), denial.Requirement!, cancellationToken);
    }
}
