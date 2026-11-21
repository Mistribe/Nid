using Nid.Banking.Domain;
using static Nid.Banking.Domain.CategorySuggestions;

namespace Nid.Banking.Tests;

/// <summary>#449: which category a pattern's hand-categorised lines point at.</summary>
public class CategorySuggestionsTests
{
    private static readonly Guid Groceries = Guid.NewGuid(), Restaurants = Guid.NewGuid(), Archived = Guid.NewGuid();

    private static IReadOnlyList<Pick> Run(params Evidence[] evidence) => Decide(evidence, id => id != Archived);

    [Fact]
    public void Two_agreeing_lines_suggest_their_category()
    {
        var pick = Assert.Single(Run(new Evidence("SHOP", true, Groceries, 2)));
        Assert.Equal(("SHOP", true, Groceries, 2, 2, 100), (pick.Pattern, pick.Debit, pick.Category, pick.Lines, pick.Total, pick.Agreement));
    }

    [Fact]
    public void One_line_is_not_enough() => Assert.Empty(Run(new Evidence("SHOP", true, Groceries, 1)));

    [Fact]
    public void Eighty_percent_agree_at_least()
    {
        var pick = Assert.Single(Run(new Evidence("SHOP", true, Groceries, 4), new Evidence("SHOP", true, Restaurants, 1)));
        Assert.Equal((Groceries, 4, 5, 80), (pick.Category, pick.Lines, pick.Total, pick.Agreement));
        Assert.Empty(Run(new Evidence("SHOP", true, Groceries, 3), new Evidence("SHOP", true, Restaurants, 1)));
    }

    [Fact]
    public void An_archived_or_merged_majority_suggests_nothing() => Assert.Empty(Run(new Evidence("SHOP", true, Archived, 3)));

    [Fact]
    public void Debits_and_credits_never_back_each_other()
    {
        Assert.Empty(Run(new Evidence("SHOP", true, Groceries, 1), new Evidence("SHOP", false, Groceries, 1)));
        var picks = Run(new Evidence("SHOP", true, Groceries, 2), new Evidence("SHOP", false, Restaurants, 2));
        Assert.Equal([(true, Groceries), (false, Restaurants)], picks.Select(p => (p.Debit, p.Category)).OrderByDescending(p => p.Debit));
    }
}
