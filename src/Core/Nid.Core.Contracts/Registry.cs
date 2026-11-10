using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public readonly record struct OwnerShare(PersonId PersonId, int ShareBp);

public sealed record NewItem(
    string Module,
    string Kind,
    string DisplayName,
    Visibility Visibility,
    string? Currency,
    IReadOnlyList<OwnerShare> Owners,
    ShareLevel ShareLevel = ShareLevel.View);

/// <summary>An owner of an item on the wire, as the lists show it so two items of the same name can be told apart.</summary>
public sealed record ItemOwnerView(Guid PersonId, int ShareBp);

public sealed record ItemAccess(ItemId Id, string DisplayName, Visibility Visibility, IReadOnlyList<Owner> Owners)
{
    public IReadOnlyList<ItemOwnerView> OwnerViews() => Owners.Select(o => new ItemOwnerView(o.PersonId.Value, o.ShareBp)).ToList();

    /// <summary>The person's share of the item in basis points, 0 when they own none of it.</summary>
    public int ShareOf(PersonId person) => Owners.FirstOrDefault(o => o.PersonId == person)?.ShareBp ?? 0;

    /// <summary>#294: <see cref="ShareLevel.View"/> for a private item.</summary>
    public ShareLevel ShareLevel { get; init; }

    /// <summary>2B1a: set when the item is archived (a closed envelope); null while it is active.</summary>
    public Instant? ArchivedAt { get; init; }

    /// <summary>2B1b: the item's currency (ISO code), null when it has none.</summary>
    public string? Currency { get; init; }

    /// <summary>The module that owns the item and its kind there (<c>subscriptions</c>, <c>subscription</c>); null when not read.</summary>
    public string? Module { get; init; }

    public string? Kind { get; init; }

    /// <summary>The <c>core.item</c> xmin, what an item delete compares (<see cref="IItemRegistry.DeleteAsync"/>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// <paramref name="IsPending"/>: an adult an admin pre-registered who has not signed in yet (no OIDC subject).
/// <paramref name="IsAdmin"/>: the technical role; it never widens Readers or Writers (authorization.md §1).
/// <paramref name="BirthDate"/> and <paramref name="RetirementAge"/> (50 to 75, 64 until set) feed the net worth projection.
/// </summary>
public sealed record PersonInfo(
    PersonId Id, PersonKind Kind, string DisplayName, string? Email, int SessionEpoch,
    bool IsAdmin = false, bool IsPending = false, LocalDate? BirthDate = null, int RetirementAge = 64, int RetirementMonths = 0);

/// <summary>1F4 design §3: what a rebinding did; <c>Changed</c> is false when the adult already had that subject.</summary>
public sealed record RebindOutcome(PersonInfo Person, string? PreviousSubject, bool Changed);

public enum AccessKind
{
    Read,
    Write,

    /// <summary>
    /// #294: what editing does not give — the item's sharing level, visibility and owners. Its owners, or any adult when every
    /// owner is a dependent (<c>AccessRules.CanCreate</c>): an adult who may only edit a shared item is refused.
    /// </summary>
    Own,
}

public interface IPersonRegistry
{
    /// <summary>
    /// Signs in an OIDC identity the identity provider let through (identity-and-access/authentication.md §5). The
    /// provider is the source of truth for <paramref name="isAdmin"/>: a known subject is refreshed and takes it; an
    /// unknown one binds the pending adult whose email matches, ignoring case; otherwise a new adult is created, unless
    /// the cap is reached (<see cref="PersonCodes.AdultCap"/>) or another adult already has that email under another
    /// subject (<see cref="PersonCodes.EmailTaken"/>: <c>rebind-subject</c> is the way to move an adult to a new subject).
    /// </summary>
    Task<Result<PersonInfo>> ProvisionAdultAsync(string oidcSubject, string displayName, string? email, bool isAdmin, CancellationToken ct);

    /// <summary>The adult bound to this OIDC subject, <c>null</c> when none is.</summary>
    Task<PersonInfo?> FindBySubjectAsync(string oidcSubject, CancellationToken ct);

    /// <summary>An admin adds an adult who will be recognised by email at their first sign-in. Pending adults count against the cap.</summary>
    Task<Result<PersonInfo>> PreRegisterAdultAsync(string displayName, string email, CancellationToken ct);

    Task<Result<PersonInfo>> CreateDependentAsync(string displayName, CancellationToken ct);

    /// <summary>Stores the person's birth date (<c>null</c> clears it) and retirement age. The ranges are the caller's to check; the table refuses what is outside them.</summary>
    Task<Result<PersonInfo>> SetRetirementAsync(PersonId id, LocalDate? birthDate, int retirementAge, int retirementMonths, CancellationToken ct);

    Task<PersonInfo?> FindAsync(PersonId id, CancellationToken ct);

    Task<IReadOnlyList<PersonId>> AdultIdsAsync(CancellationToken ct);

    Task<IReadOnlyList<PersonInfo>> ListAsync(CancellationToken ct);

    /// <summary>The person's preferences, defaults filled in; <c>null</c> when the person does not exist.</summary>
    Task<Preferences?> PreferencesAsync(PersonId id, CancellationToken ct);

    /// <summary>Applies the change to the stored preferences under the person's row lock, writes the keys it owns and keeps every other key of the stored object.</summary>
    Task<Result<Preferences>> UpdatePreferencesAsync(PersonId id, PreferencesChange change, CancellationToken ct);

    /// <summary>Removes an adult who has not signed in and owns nothing (<see cref="PersonCodes.PersonActive"/> otherwise).</summary>
    Task<Result> DeletePendingAdultAsync(PersonId id, CancellationToken ct);

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

    /// <summary>
    /// Batched reads spec §2: <see cref="FindAsync"/> for many items in two queries whatever their number, each entry built as
    /// <see cref="FindAsync"/> builds it; an unknown id is absent. A registry read: no access check.
    /// </summary>
    Task<IReadOnlyDictionary<ItemId, ItemAccess>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>
    /// The items among <paramref name="ids"/> of that module and kind whose display name contains <paramref name="nameContains"/>
    /// (typed text, case-insensitive; <c>%</c> and <c>_</c> match themselves), every one when null; built as <see cref="FindManyAsync"/>
    /// builds them. A registry read: no access check.
    /// </summary>
    Task<IReadOnlyList<ItemAccess>> SearchAsync(IReadOnlyCollection<ItemId> ids, string module, string kind, string? nameContains, CancellationToken ct);

    /// <summary>
    /// 1F3b: an import's replacement of an item it created before. Same rules as a creation (OwnershipRules); the row is
    /// updated in place, its owners reconciled (removed, changed, added), so the audit keeps the previous values.
    /// <paramref name="shareLevel"/> null keeps the item's level; a private item always ends at <see cref="ShareLevel.View"/>.
    /// Checks no access: whoever changes the visibility, the owners or the level must own the item (<see cref="IItemRules.CanCreate"/>).
    /// </summary>
    Task<Result> ReplaceAsync(ItemId id, string displayName, Visibility visibility, IReadOnlyList<OwnerShare> owners, CancellationToken ct,
        ShareLevel? shareLevel = null);

    /// <summary>
    /// #294: sets a shared item's level. A private item has none: <see cref="ShareLevel.Edit"/> on it is
    /// <see cref="ItemCodes.PrivateHasNoLevel"/>, <see cref="ShareLevel.View"/> changes nothing. Checks no access.
    /// </summary>
    Task<Result> SetShareLevelAsync(ItemId id, ShareLevel level, CancellationToken ct);

    /// <summary>2B1a: archives the item (a closed envelope). Checks no access: the caller has.</summary>
    Task<Result> ArchiveAsync(ItemId id, Instant at, CancellationToken ct);

    /// <summary>2B1a: clears the archive (an envelope reopened).</summary>
    Task<Result> UnarchiveAsync(ItemId id, CancellationToken ct);

    /// <summary>
    /// #23: removes the item and its owners (tracked removes) if its xmin is still <paramref name="version"/>,
    /// <see cref="ItemCodes.Changed"/> otherwise. Checks no access; the item's other rows are the deletion parts' (<see cref="IItemDeletions"/>).
    /// </summary>
    Task<Result> DeleteAsync(ItemId id, uint version, CancellationToken ct);
}

public static class ItemCodes
{
    /// <summary>#294: a private item has no sharing level. HTTP 422.</summary>
    public const string PrivateHasNoLevel = "core.private_has_no_level";

    /// <summary>#25: an edit removes an adult other than the actor from the owners (<see cref="IItemRules.DropsAnotherAdultOwner"/>). HTTP 422.</summary>
    public const string DropsAdultOwner = "core.drops_adult_owner";

    /// <summary>#25: an edit hides the item from an adult other than the actor who reads it today (<see cref="IItemRules.TakesReadAway"/>). HTTP 422.</summary>
    public const string TakesReadAway = "core.takes_read_away";

    /// <summary>#23: something still refers to the item being deleted (<see cref="IItemDeletionPart.BlockersAsync"/>). HTTP 409.</summary>
    public const string InUse = "core.item_in_use";

    /// <summary>#23: the item changed since its deletion preview was read. HTTP 409.</summary>
    public const string Changed = "core.item_changed";
}

public interface IAccessPolicy
{
    /// <summary>
    /// <c>NotFoundFailure</c> when the actor cannot read the item (existence is not disclosed),
    /// <c>UnauthorizedFailure</c> when the actor can read but not write (or not own, for <see cref="AccessKind.Own"/>).
    /// </summary>
    Task<Result> CheckAsync(PersonId actor, ItemId item, AccessKind kind, CancellationToken ct);

    /// <summary>
    /// Batched reads spec §2: the ids among <paramref name="ids"/> for which <see cref="CheckAsync"/> succeeds, by the same
    /// rule, in three queries whatever their number (<see cref="IItemRegistry.FindManyAsync"/> and one read of the adults).
    /// </summary>
    Task<IReadOnlySet<ItemId>> CheckManyAsync(PersonId actor, IReadOnlyCollection<ItemId> ids, AccessKind kind, CancellationToken ct);

    /// <summary>
    /// The creation rule of <c>authorization.md</c> §5: the actor is one of the owners, or every owner is a
    /// <c>dependent</c>. <c>UnauthorizedFailure</c> otherwise — there is no item yet whose existence could
    /// be disclosed, and the actor named the persons themselves.
    /// </summary>
    Task<Result> CheckCreationAsync(PersonId actor, IReadOnlyList<OwnerShare> owners, CancellationToken ct);
}
