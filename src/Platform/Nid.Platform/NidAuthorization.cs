using Microsoft.Extensions.DependencyInjection;
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
    /// the admin role anyway) and skip only the resource checks, as <see cref="ItemAccessBehavior{TRequest,TResponse}"/> does.
    /// </summary>
    public bool IsSystem => accessor.Current is { Kind: ActorKind.System };
}

public static class NidAuthorization
{
    /// <summary>
    /// Synapse's actor, role and scope checks over <see cref="IActorAccessor"/>: reads need <c>read</c>, commands
    /// <c>write:direct</c> unless they declare less (<see cref="IRequiresScope"/>), <see cref="IAdminOnly"/> needs an admin.
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
        });
    }

    /// <summary>The failures Nid answered before Synapse ran these checks: same types, codes and sentences.</summary>
    public static Failure? DenialFailure(AuthorizationDenial denial) => denial.Reason switch
    {
        AuthorizationDenialReason.Unauthenticated => new UnauthenticatedFailure("Authentication is required.", null),
        AuthorizationDenialReason.MissingRole => new UnauthorizedFailure("This action is reserved to administrators.", null)
            { Code = AdminCodes.Required },
        AuthorizationDenialReason.MissingScope => new UnauthorizedFailure($"The '{denial.Requirement}' scope is required.", null),
        _ => null,
    };
}
