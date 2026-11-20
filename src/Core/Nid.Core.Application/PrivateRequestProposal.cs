using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application;

/// <summary>
/// #384: the sole adult owner of a shared item asks to make it private, which 1F3b refuses as a direct change (or an import) because
/// another adult reads it today. The proposal is that adult's consent: they decide it, never the owner who asked
/// (<see cref="ProposalAccess"/>). Approved, the item becomes private with its owner unchanged, whoever approves it.
/// Payload <c>{ ownerId, ownerName }</c>, set from the actor at submission; not editable.
/// </summary>
public sealed class PrivateRequestProposal(
    IItemRegistry items, IItemRules rules, IPersonRegistry persons, IActorAccessor actor, IApprovingProposal approving) : IProposalHandler
{
    public const string KindName = "core.make_private";

    /// <summary>The notice that tells the owner the other adult refused (<c>private_request_refused:{proposal}</c>).</summary>
    public const string RefusedNotice = "private_request_refused";

    public string Module => "core";
    public string Kind => KindName;
    public int PayloadVersion => 1;

    /// <summary>
    /// Whether <paramref name="asker"/> may ask: the item is shared, its only owner is <paramref name="asker"/>, an adult (a private item
    /// has one owner, a dependent's item is never private), and another adult reads it today (else the change is direct).
    /// </summary>
    public static bool CanAsk(PersonId asker, ItemAccess item, IItemRules rules, IReadOnlyCollection<PersonId> adults)
        => item.Visibility == Visibility.Shared && item.Owners is [{ Kind: PersonKind.Adult } owner] && owner.PersonId == asker
           && rules.TakesReadAway(asker, Visibility.Shared, item.Owners, Visibility.Private, item.Owners, adults);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        // Submitted, the owner is the actor; approved by the other adult, the owner is the one the request names.
        var asker = approving.Id is null ? actor.Current?.PersonId : OwnerOf(payload);
        if (asker is not { } owner || await items.FindAsync(target, ct) is not { } item || !CanAsk(owner, item, rules, await persons.AdultIdsAsync(ct)))
            return Result.Failure<ProposalCheck>(new Failure(ItemCodes.PrivateRequestRefused,
                "Only the sole adult owner of a shared item another adult reads asks to make it private.", null));
        var name = (await persons.FindAsync(owner, ct))?.DisplayName ?? "";
        return Result.Success(new ProposalCheck(null, [], Element(new JsonObject { ["ownerId"] = owner.Value.ToString("D"), ["ownerName"] = name })));
    }

    public Failure? RefuseEdit(JsonElement submitted, JsonElement edited)
        => new(ItemCodes.PrivateRequestRefused, "A request to make an item private is approved or rejected as it is.", null);

    public Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Task.FromResult(Element(new JsonObject()));

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (await items.FindAsync(target, ct) is not { } item)
            return Result.Failure<IReadOnlyList<Guid>>(new NotFoundFailure("item", target.ToString()));
        var replaced = await items.ReplaceAsync(item.Id, item.DisplayName, Visibility.Private,
            item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct, ShareLevel.View);
        return replaced.TryGetFailure(out var failure)
            ? Result.Failure<IReadOnlyList<Guid>>(failure!)
            : Result.Success<IReadOnlyList<Guid>>([]);
    }

    private static PersonId? OwnerOf(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("ownerId", out var id) && id.ValueKind == JsonValueKind.String
           && id.TryGetGuid(out var guid) ? new PersonId(guid) : null;

    private static JsonElement Element(JsonObject json)
    {
        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.Clone();
    }
}
