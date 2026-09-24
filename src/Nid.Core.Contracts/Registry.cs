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

public sealed record ItemAccess(ItemId Id, string DisplayName, Visibility Visibility, IReadOnlyList<Owner> Owners);

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

    /// <summary>The person's preferences, defaults filled in; <c>null</c> when the person does not exist.</summary>
    Task<Preferences?> PreferencesAsync(PersonId id, CancellationToken ct);

    /// <summary>Writes the three keys it owns and keeps every other key of the stored object.</summary>
    Task SavePreferencesAsync(PersonId id, Preferences preferences, CancellationToken ct);
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

    /// <summary>
    /// The creation rule of <c>authorization.md</c> §5: the actor is one of the owners, or every owner is a
    /// <c>dependent</c>. <c>UnauthorizedFailure</c> otherwise — there is no item yet whose existence could
    /// be disclosed, and the actor named the persons themselves.
    /// </summary>
    Task<Result> CheckCreationAsync(PersonId actor, IReadOnlyList<OwnerShare> owners, CancellationToken ct);
}
