using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public readonly record struct OwnerShare(PersonId PersonId, int ShareBp);

public sealed record NewItem(
    string Module,
    string Kind,
    string DisplayName,
    Visibility Visibility,
    string? Currency,
    IReadOnlyList<OwnerShare> Owners);

public sealed record ItemAccess(ItemId Id, Visibility Visibility, IReadOnlyList<Owner> Owners);

public sealed record PersonInfo(PersonId Id, PersonKind Kind, string DisplayName, string? Email, int SessionEpoch);

public enum AccessKind
{
    Read,
    Write,
}

public interface IPersonRegistry
{
    /// <summary>Finds the adult for an OIDC subject or creates it. Refused once two adults exist.</summary>
    Task<Result<PersonInfo>> ProvisionAdultAsync(string oidcSubject, string displayName, string? email, CancellationToken ct);

    Task<Result<PersonInfo>> CreateDependentAsync(string displayName, CancellationToken ct);

    Task<PersonInfo?> FindAsync(PersonId id, CancellationToken ct);

    Task<IReadOnlyList<PersonId>> AdultIdsAsync(CancellationToken ct);

    Task<IReadOnlyList<PersonInfo>> ListAsync(CancellationToken ct);
}

public interface IItemRegistry
{
    Task<Result<ItemId>> CreateAsync(NewItem item, CancellationToken ct);

    Task<ItemAccess?> FindAsync(ItemId id, CancellationToken ct);
}

public interface IAccessPolicy
{
    /// <summary>
    /// <c>NotFoundFailure</c> when the actor cannot read the item (existence is not disclosed),
    /// <c>UnauthorizedFailure</c> when the actor can read but not write.
    /// </summary>
    Task<Result> CheckAsync(PersonId actor, ItemId item, AccessKind kind, CancellationToken ct);
}
