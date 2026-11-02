using Nid.Core.Contracts;
using Nid.Core.Domain;

namespace Nid.Host.IntegrationTests.LeakBench;

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

    /// <summary>Every shared shape at both levels (#294); a private item is stored at view.</summary>
    public static TheoryData<Visibility, ShareLevel, Owner[]> Items()
    {
        var data = new TheoryData<Visibility, ShareLevel, Owner[]>();
        Owner Adult(PersonId p, int share) => new(p, PersonKind.Adult, share);
        Owner Kid(int share) => new(Dependent, PersonKind.Dependent, share);

        foreach (var level in new[] { ShareLevel.View, ShareLevel.Edit })
        {
            data.Add(Visibility.Shared, level, [Adult(AdultA, 10_000)]);
            data.Add(Visibility.Shared, level, [Adult(AdultB, 10_000)]);
            data.Add(Visibility.Shared, level, [Adult(AdultA, 5_000), Adult(AdultB, 5_000)]);
            data.Add(Visibility.Shared, level, [Kid(10_000)]);
            data.Add(Visibility.Shared, level, [Adult(AdultA, 5_000), Kid(5_000)]);
        }
        data.Add(Visibility.Private, ShareLevel.View, [Adult(AdultA, 10_000)]);
        data.Add(Visibility.Private, ShareLevel.View, [Adult(AdultB, 10_000)]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void A_private_item_is_readable_only_by_its_owner(Visibility visibility, ShareLevel level, Owner[] owners)
    {
        var readers = AccessRules.Readers(visibility, owners, Adults);

        if (visibility == Visibility.Private)
        {
            Assert.Equal(owners.Select(o => o.PersonId).ToHashSet(), readers);
            Assert.Equal(readers, AccessRules.Writers(visibility, level, owners, Adults));
        }
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void Writers_are_always_a_subset_of_readers(Visibility visibility, ShareLevel level, Owner[] owners)
    {
        var readers = AccessRules.Readers(visibility, owners, Adults);
        var writers = AccessRules.Writers(visibility, level, owners, Adults);

        Assert.True(writers.IsSubsetOf(readers), $"writers {string.Join(',', writers)} ⊄ readers {string.Join(',', readers)}");
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void An_item_owned_by_a_dependent_is_written_by_both_adults(Visibility visibility, ShareLevel level, Owner[] owners)
    {
        if (!owners.Any(o => o.Kind == PersonKind.Dependent))
            return;

        Assert.Equal(Adults.ToHashSet(), AccessRules.Writers(visibility, level, owners, Adults));
    }

    /// <summary>#294: the level decides whether a non-owner adult writes a shared item; it never makes them an owner.</summary>
    [Theory]
    [MemberData(nameof(Items))]
    public void A_non_owner_adult_writes_a_shared_item_only_at_the_edit_level(Visibility visibility, ShareLevel level, Owner[] owners)
    {
        if (owners.Any(o => o.Kind == PersonKind.Dependent))
            return;

        var writers = AccessRules.Writers(visibility, level, owners, Adults);
        foreach (var adult in Adults.Where(a => owners.All(o => o.PersonId != a)))
        {
            Assert.Equal(visibility == Visibility.Shared && level == ShareLevel.Edit, writers.Contains(adult));
            Assert.False(AccessRules.CanCreate(adult, owners));
        }
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
