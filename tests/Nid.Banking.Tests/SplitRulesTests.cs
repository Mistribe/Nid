using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using UnambitiousFx.Functional;

namespace Nid.Banking.Tests;

/// <summary>#84: a split's parts are 2 to 20, each of the line's sign, and add up to the line.</summary>
public class SplitRulesTests
{
    private static PartInput Part(decimal amount) => new(amount, null, null);

    private static string? CodeOf(Result result) => result.TryGetFailure(out var failure) ? failure.Code : null;

    [Fact]
    public void An_exact_sum_is_accepted() => Assert.True(SplitRules.Validate(-100m, [Part(-60m), Part(-40m)]).IsSuccess);

    [Fact]
    public void A_sum_off_by_a_cent_is_refused()
    {
        Assert.Equal(BankingCodes.SplitSum, CodeOf(SplitRules.Validate(-100m, [Part(-60m), Part(-40.01m)])));
        Assert.Equal(BankingCodes.SplitSum, CodeOf(SplitRules.Validate(-100m, [Part(-60m), Part(-39.99m)])));
    }

    [Fact]
    public void A_zero_an_opposite_sign_or_over_precise_part_is_refused()
    {
        Assert.Equal(BankingCodes.SplitPart, CodeOf(SplitRules.Validate(-100m, [Part(-100m), Part(0m)])));
        Assert.Equal(BankingCodes.SplitPart, CodeOf(SplitRules.Validate(-100m, [Part(-120m), Part(20m)])));
        Assert.Equal(BankingCodes.SplitPart, CodeOf(SplitRules.Validate(100m, [Part(-20m), Part(120m)])));
        Assert.Equal(BankingCodes.SplitPart, CodeOf(SplitRules.Validate(-100m, [Part(-50.00001m), Part(-49.99999m)])));
    }

    [Fact]
    public void One_or_twenty_one_parts_are_refused_and_twenty_are_accepted()
    {
        Assert.Equal(BankingCodes.SplitPart, CodeOf(SplitRules.Validate(-100m, [Part(-100m)])));
        Assert.Equal(BankingCodes.SplitPart, CodeOf(SplitRules.Validate(-21m, [.. Enumerable.Repeat(Part(-1m), 21)])));
        Assert.True(SplitRules.Validate(-20m, [.. Enumerable.Repeat(Part(-1m), 20)]).IsSuccess);
    }

    [Fact]
    public void Nothing_replaces_a_split_category()
    {
        foreach (var incoming in Enum.GetValues<CategorySource>())
            Assert.False(CategoryPrecedence.CanReplace(CategorySource.Split, incoming, byPerson: true));
        Assert.True(CategoryPrecedence.CanReplace(null, CategorySource.Hint, byPerson: false));
    }
}
