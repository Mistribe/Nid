using Nid.Core.Contracts;
using UnambitiousFx.Functional;

namespace Nid.TestSupport;

public static class PersonRegistryExtensions
{
    /// <summary>A sign-in of an adult the identity provider did not make an admin: what most fixtures need.</summary>
    public static Task<Result<PersonInfo>> ProvisionAdultAsync(
        this IPersonRegistry registry, string oidcSubject, string displayName, string? email, CancellationToken ct)
        => registry.ProvisionAdultAsync(oidcSubject, displayName, email, isAdmin: false, ct);
}
