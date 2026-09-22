using Nid.Core.Contracts;
using Nid.Core.Domain;

namespace Nid.Core.Tests;

public class AccessRulesTests
{
    private static readonly PersonId Alice = PersonId.New();
    private static readonly PersonId Bob = PersonId.New();
    private static readonly PersonId Kid = PersonId.New();
    private static readonly PersonId[] Adults = [Alice, Bob];

    private static Owner Adult(PersonId id, int bp) => new(id, PersonKind.Adult, bp);

    private static Owner Child(int bp) => new(Kid, PersonKind.Dependent, bp);

    [Fact]
    public void Shared_item_with_one_owner_is_read_by_both_and_written_by_the_owner()
    {
        IReadOnlyList<Owner> owners = [Adult(Alice, 10_000)];
        Assert.Equal(Adults.ToHashSet(), AccessRules.Readers(Visibility.Shared, owners, Adults));
        Assert.Equal(new HashSet<PersonId> { Alice }, AccessRules.Writers(Visibility.Shared, owners, Adults));
    }

    [Fact]
    public void Shared_item_with_two_owners_is_written_by_both()
    {
        IReadOnlyList<Owner> owners = [Adult(Alice, 5_000), Adult(Bob, 5_000)];
        Assert.Equal(Adults.ToHashSet(), AccessRules.Writers(Visibility.Shared, owners, Adults));
    }

    [Fact]
    public void Private_item_is_read_and_written_by_its_owner_only()
    {
        IReadOnlyList<Owner> owners = [Adult(Alice, 10_000)];
        var only = new HashSet<PersonId> { Alice };
        Assert.Equal(only, AccessRules.Readers(Visibility.Private, owners, Adults));
        Assert.Equal(only, AccessRules.Writers(Visibility.Private, owners, Adults));
    }

    [Fact]
    public void Item_owned_by_a_dependent_is_managed_by_both_adults()
    {
        IReadOnlyList<Owner> owners = [Child(10_000)];
        Assert.Equal(Adults.ToHashSet(), AccessRules.Writers(Visibility.Shared, owners, Adults));

        IReadOnlyList<Owner> mixed = [Adult(Alice, 5_000), Child(5_000)];
        Assert.Equal(Adults.ToHashSet(), AccessRules.Writers(Visibility.Shared, mixed, Adults));
    }

    [Fact]
    public void Writers_are_always_readers_and_never_empty_for_valid_items()
    {
        var random = new Random(20260919);
        for (var i = 0; i < 5_000; i++)
        {
            var (visibility, owners) = RandomValidItem(random);
            Assert.True(OwnershipRules.Validate(visibility, owners).IsSuccess);

            var readers = AccessRules.Readers(visibility, owners, Adults);
            var writers = AccessRules.Writers(visibility, owners, Adults);

            Assert.NotEmpty(writers);
            Assert.True(writers.IsSubsetOf(readers), $"writers must be readers: {visibility} {string.Join(",", owners)}");
            Assert.True(readers.IsSubsetOf(Adults.ToHashSet()));
        }
    }

    [Fact]
    public void A_private_item_never_leaks_to_the_other_adult()
    {
        var random = new Random(7);
        for (var i = 0; i < 5_000; i++)
        {
            var (visibility, owners) = RandomValidItem(random);
            if (visibility != Visibility.Private)
                continue;

            var other = owners[0].PersonId == Alice ? Bob : Alice;
            Assert.DoesNotContain(other, AccessRules.Readers(visibility, owners, Adults));
            Assert.DoesNotContain(other, AccessRules.Writers(visibility, owners, Adults));
        }
    }

    private static (Visibility, IReadOnlyList<Owner>) RandomValidItem(Random random)
    {
        var shape = random.Next(5);
        return shape switch
        {
            0 => (Visibility.Private, [Adult(random.Next(2) == 0 ? Alice : Bob, 10_000)]),
            1 => (Visibility.Shared, [Adult(random.Next(2) == 0 ? Alice : Bob, 10_000)]),
            2 => (Visibility.Shared, SplitBetweenAdults(random.Next(1, 10_000))),
            3 => (Visibility.Shared, [Child(10_000)]),
            _ => (Visibility.Shared, [Adult(Alice, 6_000), Child(4_000)]),
        };
    }

    private static IReadOnlyList<Owner> SplitBetweenAdults(int aliceBp) => [Adult(Alice, aliceBp), Adult(Bob, 10_000 - aliceBp)];
}

public class OwnershipRulesTests
{
    private static Owner Adult(int bp) => new(PersonId.New(), PersonKind.Adult, bp);

    private static string? FailureCode(UnambitiousFx.Functional.Result result)
        => result.TryGetFailure(out var failure) ? failure!.Code : null;

    [Fact]
    public void Valid_shared_split_is_accepted()
        => Assert.True(OwnershipRules.Validate(Visibility.Shared, [Adult(5_000), Adult(5_000)]).IsSuccess);

    [Fact]
    public void Owners_are_required()
        => Assert.Equal("core.owner_required", FailureCode(OwnershipRules.Validate(Visibility.Shared, [])));

    [Fact]
    public void Shares_must_add_up_to_ten_thousand()
        => Assert.Equal("core.share_sum", FailureCode(OwnershipRules.Validate(Visibility.Shared, [Adult(5_000), Adult(4_999)])));

    [Fact]
    public void Share_range_is_checked()
        => Assert.Equal("core.share_range", FailureCode(OwnershipRules.Validate(Visibility.Shared, [Adult(10_001)])));

    [Fact]
    public void Private_item_needs_exactly_one_owner()
        => Assert.Equal("core.private_single_owner",
            FailureCode(OwnershipRules.Validate(Visibility.Private, [Adult(5_000), Adult(5_000)])));

    [Fact]
    public void Dependent_only_item_cannot_be_private()
    {
        var kid = new Owner(PersonId.New(), PersonKind.Dependent, 10_000);
        Assert.Equal("core.private_dependent", FailureCode(OwnershipRules.Validate(Visibility.Private, [kid])));
    }

    [Fact]
    public void The_same_person_cannot_appear_twice()
    {
        var person = PersonId.New();
        Owner[] owners = [new(person, PersonKind.Adult, 5_000), new(person, PersonKind.Adult, 5_000)];
        Assert.Equal("core.owner_duplicate", FailureCode(OwnershipRules.Validate(Visibility.Shared, owners)));
    }
}
