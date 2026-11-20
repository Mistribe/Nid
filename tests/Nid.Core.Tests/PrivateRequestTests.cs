using System.Reflection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Core.Tests;

/// <summary>
/// #384: who may ask the other adult to make a shared item private, and the 1F3b refusal the request leaves as it was for a direct
/// change and an import.
/// </summary>
public class PrivateRequestTests
{
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();
    private static readonly PersonId[] Adults = [Alice, Bob];
    private static readonly ItemRules Rules = new();

    private static ItemAccess Item(Visibility visibility, params Owner[] owners) => new(ItemId.New(), "Compte", visibility, owners);

    private static Owner Adult(PersonId id, int bp = 10_000) => new(id, PersonKind.Adult, bp);

    [Fact]
    public void Only_the_sole_adult_owner_of_a_shared_item_another_adult_reads_may_ask()
    {
        Assert.True(PrivateRequestProposal.CanAsk(Alice, Item(Visibility.Shared, Adult(Alice)), Rules, Adults));

        Assert.False(PrivateRequestProposal.CanAsk(Bob, Item(Visibility.Shared, Adult(Alice)), Rules, Adults));
        Assert.False(PrivateRequestProposal.CanAsk(Alice, Item(Visibility.Shared, Adult(Alice, 5_000), Adult(Bob, 5_000)), Rules, Adults));
        Assert.False(PrivateRequestProposal.CanAsk(Alice, Item(Visibility.Shared, new Owner(PersonId.New(), PersonKind.Dependent, 10_000)), Rules, Adults));
        Assert.False(PrivateRequestProposal.CanAsk(Alice, Item(Visibility.Private, Adult(Alice)), Rules, Adults));
        // Nobody else reads it: the change is direct, there is no one to ask.
        Assert.False(PrivateRequestProposal.CanAsk(Alice, Item(Visibility.Shared, Adult(Alice)), Rules, [Alice]));
    }

    [Fact]
    public async Task A_direct_change_to_private_is_still_refused()
    {
        var item = Item(Visibility.Shared, Adult(Alice));

        var refusal = await OwnershipEdit.RefusalAsync(Rules, Stub<IPersonRegistry>(_ => Task.FromResult<IReadOnlyList<PersonId>>(Adults)),
            Alice, item, OwnershipEdit.Of(item, Visibility.Private, null, null), "envelope", TestContext.Current.CancellationToken);

        Assert.Equal(ItemCodes.TakesReadAway, refusal?.Code);
    }

    [Fact]
    public async Task An_import_replacing_it_private_is_still_refused()
    {
        var item = Item(Visibility.Shared, Adult(Alice));

        var issue = await ImportSectionKit.ReplaceRefusalAsync(Stub<IAccessPolicy>(_ => Task.FromResult(Result.Success())), Rules, Alice, item.Id, item,
            Visibility.Private, ShareLevel.View, item.Owners, null, "envelope", "envelopes[0]",
            () => Task.FromResult<IReadOnlyList<PersonId>>(Adults), TestContext.Current.CancellationToken);

        Assert.Equal((ImportCodes.NotPermitted, "envelopes[0].visibility"), (issue?.Code, issue?.Path));
    }

    /// <summary>An interface whose every call <paramref name="answer"/> answers (the calls these rules make are one each).</summary>
    private static T Stub<T>(Func<MethodInfo, object?> answer) where T : class
    {
        var stub = DispatchProxy.Create<T, Answering>();
        ((Answering)(object)stub).Answer = answer;
        return stub;
    }

    public class Answering : DispatchProxy
    {
        public Func<MethodInfo, object?> Answer { get; set; } = _ => throw new NotSupportedException();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Answer(targetMethod!);
    }
}
