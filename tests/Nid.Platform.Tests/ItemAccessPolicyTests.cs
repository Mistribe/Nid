using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using UnambitiousFx.Synapse.Authorization;

namespace Nid.Platform.Tests;

/// <summary>Stage 4 of authorization.md §2 on Synapse's resource check: hidden (404) when unreadable, denied (403) when read-only.</summary>
public class ItemAccessPolicyTests
{
    private static readonly ItemId Item = new(Guid.NewGuid());

    private sealed record EditItem(ItemId ItemId) : ICommand<int>, IItemScoped
    {
        public AccessKind Access => AccessKind.Write;
    }

    private sealed record ProposeChange(ItemId? ItemId) : ICommand<int>, IOptionalItemScoped
    {
        public AccessKind Access => AccessKind.Own;
    }

    /// <summary>Answers <see cref="CheckAsync"/> with <paramref name="answer"/> and remembers what it was asked.</summary>
    private sealed class FakePolicy(Result answer) : IAccessPolicy
    {
        public (PersonId Actor, ItemId Item, AccessKind Kind)? Asked { get; private set; }

        public Task<Result> CheckAsync(PersonId actor, ItemId item, AccessKind kind, CancellationToken ct)
        {
            Asked = (actor, item, kind);
            return Task.FromResult(answer);
        }

        public Task<IReadOnlySet<ItemId>> CheckManyAsync(PersonId actor, IReadOnlyCollection<ItemId> ids, AccessKind kind, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Result> CheckCreationAsync(PersonId actor, IReadOnlyList<OwnerShare> owners, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeDenials : IAccessDenials
    {
        public List<(PersonId Person, ItemId? Item, string Access)> Recorded { get; } = [];

        public Task RecordAsync(PersonId person, ItemId? item, string access, CancellationToken ct)
        {
            Recorded.Add((person, item, access));
            return Task.CompletedTask;
        }
    }

    private static async Task<(ResourceAccess Access, FakePolicy Policy)> CheckAsync(Actor? actor, Result answer, IResourceScoped request)
    {
        var accessor = new ActorAccessor { Current = actor };
        var policy = new FakePolicy(answer);
        var access = await new ItemAccessPolicy(accessor, policy).CheckAsync(new ActorPrincipal(accessor), request, TestContext.Current.CancellationToken);
        return (access, policy);
    }

    [Fact]
    public async Task An_item_the_policy_lets_through_is_allowed_and_asked_with_the_actor_and_the_access()
    {
        var person = PersonId.New();

        var (access, policy) = await CheckAsync(Actor.Browser(person), Result.Success(), new EditItem(Item));

        Assert.Equal(ResourceAccess.Allowed, access);
        Assert.Equal((person, Item, AccessKind.Write), policy.Asked);
    }

    [Fact]
    public async Task An_item_the_actor_cannot_read_is_hidden()
    {
        var (access, _) = await CheckAsync(Actor.Browser(PersonId.New()),
            Result.Failure(new NotFoundFailure("item", Item.ToString())), new EditItem(Item));

        Assert.Equal(ResourceAccess.Hidden, access);
    }

    [Fact]
    public async Task An_item_the_actor_reads_but_cannot_change_is_denied()
    {
        var (access, _) = await CheckAsync(Actor.Browser(PersonId.New()),
            Result.Failure(new UnauthorizedFailure("You can read this item but not change it.", null)), new EditItem(Item));

        Assert.Equal(ResourceAccess.Denied, access);
    }

    [Fact]
    public async Task An_optional_item_is_checked_with_its_own_access()
    {
        var (access, policy) = await CheckAsync(Actor.Browser(PersonId.New()), Result.Success(), new ProposeChange(Item));

        Assert.Equal(ResourceAccess.Allowed, access);
        Assert.Equal((Item, AccessKind.Own), (policy.Asked!.Value.Item, policy.Asked.Value.Kind));
    }

    [Fact]
    public async Task An_actor_bound_to_no_person_is_hidden_without_asking()
    {
        var anonymousPerson = new Actor(ActorKind.Person, null, ActorOrigin.Api, new HashSet<string> { ScopeNames.WriteDirect });

        var (access, policy) = await CheckAsync(anonymousPerson, Result.Success(), new EditItem(Item));

        Assert.Equal(ResourceAccess.Hidden, access);
        Assert.Null(policy.Asked);
    }

    [Fact]
    public void Synapse_reads_the_item_and_the_access_on_the_wire()
    {
        IResourceScoped scoped = new EditItem(Item);
        IOptionallyResourceScoped none = new ProposeChange(null);

        Assert.Equal((Item.Value.ToString(), "write"), (scoped.ResourceId, scoped.Access));
        Assert.Null(none.OptionalResourceId);
        Assert.Equal("own", none.Access);
    }

    [Theory]
    [InlineData(AuthorizationDenialReason.ResourceHidden)]
    [InlineData(AuthorizationDenialReason.ResourceAccessDenied)]
    public async Task A_hidden_or_denied_item_is_recorded_with_the_access_asked(AuthorizationDenialReason reason)
    {
        var person = PersonId.New();
        var denials = new FakeDenials();

        await new ItemAccessDenials(new ActorAccessor { Current = Actor.Browser(person) }, denials)
            .RecordAsync(new AuthorizationDenial(new EditItem(Item), reason, person.Value.ToString(), "write", Item.Value.ToString()), TestContext.Current.CancellationToken);

        Assert.Equal([(person, (ItemId?)Item, "write")], denials.Recorded);
    }

    [Fact]
    public async Task A_scope_denial_is_not_an_item_access_denial()
    {
        var denials = new FakeDenials();

        await new ItemAccessDenials(new ActorAccessor { Current = Actor.Browser(PersonId.New()) }, denials)
            .RecordAsync(new AuthorizationDenial(new EditItem(Item), AuthorizationDenialReason.MissingScope, null, ScopeNames.WriteDirect), TestContext.Current.CancellationToken);

        Assert.Empty(denials.Recorded);
    }

    [Fact]
    public void A_hidden_item_answers_the_not_found_it_answered_before()
    {
        var failure = NidAuthorization.DenialFailure(new AuthorizationDenial(new EditItem(Item), AuthorizationDenialReason.ResourceHidden,
            null, "write", Item.Value.ToString()));

        var expected = new NotFoundFailure("item", Item.ToString());
        var notFound = Assert.IsType<NotFoundFailure>(failure);
        Assert.Equal((expected.Code, expected.Message), (notFound.Code, notFound.Message));
    }

    [Theory]
    [InlineData("write", "You can read this item but not change it.")]
    [InlineData("own", "Only the item's owners change its sharing, its visibility or its owners.")]
    public void A_denied_item_answers_the_sentence_of_its_access(string access, string message)
    {
        var failure = NidAuthorization.DenialFailure(new AuthorizationDenial(new EditItem(Item), AuthorizationDenialReason.ResourceAccessDenied,
            null, access, Item.Value.ToString()));

        Assert.IsType<UnauthorizedFailure>(failure);
        Assert.Equal(message, failure!.Message);
    }
}
