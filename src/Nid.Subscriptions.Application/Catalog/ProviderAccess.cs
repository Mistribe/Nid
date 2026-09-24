using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// Design §3.5, in one place for the list, every write and the import: a catalog provider is visible to every adult; a
/// custom or adopted one to an adult who can see a subscription using it; a custom one to its creator while no
/// subscription visible to them uses it (amended after 1G: an archived subscription is not visible, so it must not
/// hide its provider from its own creator). Derived from <see cref="IVisibleItems"/> (scope Me: shared items and the actor's own), like every list. A
/// provider the actor cannot see is answered exactly like one that does not exist.
/// </summary>
public sealed class ProviderAccess(IVisibleItems visible, IProviderStore providers)
{
    public async Task<IReadOnlyList<Provider>> ListAsync(PersonId actor, string? nameContains, CancellationToken ct)
        => await providers.VisibleAsync(actor, await visible.ForAsync(actor, VisibilityScope.Me, ct), nameContains, ct);

    /// <summary>The providers among <paramref name="keys"/> the actor can see; all of them for the system actor (no person).</summary>
    public async Task<IReadOnlyDictionary<string, Provider>> FindAsync(PersonId? actor, IReadOnlyCollection<string> keys, CancellationToken ct)
        => actor is { } person
            ? await providers.FindVisibleAsync(person, await visible.ForAsync(person, VisibilityScope.Me, ct), keys, ct)
            : await providers.FindAsync(keys, ct);

    /// <summary>Design §5: a new link names a provider visible to the actor and not archived; anything else, the same refusal.</summary>
    public async Task<Result> EnsureLinkableAsync(PersonId? actor, string key, CancellationToken ct)
        => (await FindAsync(actor, [key], ct)).TryGetValue(key, out var provider) && !provider.IsArchived
            ? Result.Success()
            : Result.Failure(UnknownProvider());

    /// <summary>One message for "does not exist", "cannot be seen" and "archived": nothing tells them apart.</summary>
    public static Failure UnknownProvider()
        => new(CatalogCodes.UnknownProvider, "This provider does not exist or cannot be linked.", null);
}
