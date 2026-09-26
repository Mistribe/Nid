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

/// <summary>
/// <paramref name="IsPending"/>: an adult an admin pre-registered who has not signed in yet (no OIDC subject).
/// <paramref name="IsAdmin"/>: the technical role; it never widens Readers or Writers (authorization.md §1).
/// </summary>
public sealed record PersonInfo(
    PersonId Id, PersonKind Kind, string DisplayName, string? Email, int SessionEpoch,
    bool IsAdmin = false, bool IsPending = false);

/// <summary>1F4 design §3: what a rebinding did; <c>Changed</c> is false when the adult already had that subject.</summary>
public sealed record RebindOutcome(PersonInfo Person, string? PreviousSubject, bool Changed);

public enum AccessKind
{
    Read,
    Write,
}

public interface IPersonRegistry
{
    /// <summary>
    /// Signs in an OIDC identity (identity-and-access/authentication.md §5): a known subject is refreshed; with no
    /// adult yet, the identity becomes the first adult and admin; otherwise it binds the pending adult whose email
    /// matches, ignoring case, or fails with <see cref="PersonCodes.NotRegistered"/>.
    /// </summary>
    Task<Result<PersonInfo>> ProvisionAdultAsync(string oidcSubject, string displayName, string? email, CancellationToken ct);

    /// <summary>An admin adds an adult who will be recognised by email at their first sign-in. Pending adults count against the cap.</summary>
    Task<Result<PersonInfo>> PreRegisterAdultAsync(string displayName, string email, CancellationToken ct);

    Task<Result<PersonInfo>> CreateDependentAsync(string displayName, CancellationToken ct);

    Task<PersonInfo?> FindAsync(PersonId id, CancellationToken ct);

    Task<IReadOnlyList<PersonId>> AdultIdsAsync(CancellationToken ct);

    Task<IReadOnlyList<PersonInfo>> ListAsync(CancellationToken ct);

    /// <summary>The person's preferences, defaults filled in; <c>null</c> when the person does not exist.</summary>
    Task<Preferences?> PreferencesAsync(PersonId id, CancellationToken ct);

    /// <summary>Writes the three keys it owns and keeps every other key of the stored object.</summary>
    Task SavePreferencesAsync(PersonId id, Preferences preferences, CancellationToken ct);

    /// <summary>Removes an adult who has not signed in and owns nothing (<see cref="PersonCodes.PersonActive"/> otherwise).</summary>
    Task<Result> DeletePendingAdultAsync(PersonId id, CancellationToken ct);

    /// <summary>Promotes or demotes an adult; at least one admin who has signed in always remains (<see cref="PersonCodes.LastAdmin"/>).</summary>
    Task<Result<PersonInfo>> SetAdminAsync(PersonId id, bool isAdmin, CancellationToken ct);

    /// <summary>
    /// Binds an adult to a new OIDC subject and ends their sessions in the same write (1F4 design §3). A subject held by
    /// another person is <see cref="PersonCodes.SubjectTaken"/>, with that person's id in the metadata's <c>person</c>.
    /// </summary>
    Task<Result<RebindOutcome>> RebindSubjectAsync(PersonId id, string subject, CancellationToken ct);

    /// <summary>"Sign out everywhere": increments the adult's session epoch, so every cookie they hold is refused.</summary>
    Task<Result<PersonInfo>> RevokeSessionsAsync(PersonId id, CancellationToken ct);
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
