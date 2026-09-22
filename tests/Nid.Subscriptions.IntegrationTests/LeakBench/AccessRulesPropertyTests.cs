using Nid.Core.Contracts;
using Nid.Core.Domain;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

/// <summary>
/// Properties of access-tests.md §4, checked over generated items rather than one fixture. These prove the
/// rules are self-consistent; that they are the RIGHT rules is reviewed against core-data-model.md §4.3.
/// Two of §4's properties are not here: <c>ChangeOwners</c> does not exist yet, and "the household totals
/// depend on no private item" is a property of a read, checked over the real host in <c>LeakBenchTests</c>.
/// </summary>
public class AccessRulesPropertyTests
{
    private static readonly PersonId AdultA = new(Guid.CreateVersion7());
    private static readonly PersonId AdultB = new(Guid.CreateVersion7());
    private static readonly PersonId Dependent = new(Guid.CreateVersion7());
    private static readonly PersonId[] Adults = [AdultA, AdultB];

    public static TheoryData<Visibility, Owner[]> Items()
    {
        var data = new TheoryData<Visibility, Owner[]>();
        Owner Adult(PersonId p, int share) => new(p, PersonKind.Adult, share);
        Owner Kid(int share) => new(Dependent, PersonKind.Dependent, share);

        data.Add(Visibility.Shared, [Adult(AdultA, 10_000)]);
        data.Add(Visibility.Shared, [Adult(AdultB, 10_000)]);
        data.Add(Visibility.Shared, [Adult(AdultA, 5_000), Adult(AdultB, 5_000)]);
        data.Add(Visibility.Shared, [Kid(10_000)]);
        data.Add(Visibility.Shared, [Adult(AdultA, 5_000), Kid(5_000)]);
        data.Add(Visibility.Private, [Adult(AdultA, 10_000)]);
        data.Add(Visibility.Private, [Adult(AdultB, 10_000)]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void A_private_item_is_readable_only_by_its_owner(Visibility visibility, Owner[] owners)
    {
        var readers = AccessRules.Readers(visibility, owners, Adults);

        if (visibility == Visibility.Private)
            Assert.Equal(owners.Select(o => o.PersonId).ToHashSet(), readers);
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void Writers_are_always_a_subset_of_readers(Visibility visibility, Owner[] owners)
    {
        var readers = AccessRules.Readers(visibility, owners, Adults);
        var writers = AccessRules.Writers(visibility, owners, Adults);

        Assert.True(writers.IsSubsetOf(readers), $"writers {string.Join(',', writers)} ⊄ readers {string.Join(',', readers)}");
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void An_item_owned_by_a_dependent_is_written_by_both_adults(Visibility visibility, Owner[] owners)
    {
        if (!owners.Any(o => o.Kind == PersonKind.Dependent))
            return;

        Assert.Equal(Adults.ToHashSet(), AccessRules.Writers(visibility, owners, Adults));
    }

    [Fact]
    public void Turning_an_item_private_and_back_restores_its_readers()
    {
        Owner[] owners = [new(AdultA, PersonKind.Adult, 10_000)];

        var before = AccessRules.Readers(Visibility.Shared, owners, Adults);
        var whilePrivate = AccessRules.Readers(Visibility.Private, owners, Adults);
        var after = AccessRules.Readers(Visibility.Shared, owners, Adults);

        Assert.Equal(before, after);
        Assert.NotEqual(before, whilePrivate);
    }
}
