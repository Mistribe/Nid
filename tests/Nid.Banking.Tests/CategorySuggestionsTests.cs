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

    private static Pick? Near(params Neighbour[] neighbours)
        => ByMeaning("NEW SHOP", true, neighbours,
            [new Evidence("SUPER SHOP", true, Groceries, 2), new Evidence("FOOD SHOP", true, Groceries, 3), new Evidence("BISTRO", true, Restaurants, 2),
             new Evidence("SUPER SHOP", false, Restaurants, 5)],
            id => id != Archived, MinSimilarity);

    // The model's profile threshold, here as a household's calibration could set it.
    private const double MinSimilarity = 0.8;

    [Fact]
    public void Patterns_near_by_meaning_decide_on_the_same_thresholds()
    {
        var pick = Near(new Neighbour("NEW SHOP", "SUPER SHOP", 0.9), new Neighbour("NEW SHOP", "FOOD SHOP", 0.85))!;
        Assert.Equal((Groceries, 5, 5, SuggestionBases.Meaning), (pick.Category, pick.Lines, pick.Total, pick.Basis));
        // Too far: not counted. A disagreeing neighbour brings the agreement under 80 %.
        Assert.Null(Near(new Neighbour("NEW SHOP", "SUPER SHOP", MinSimilarity - 0.01)));
        Assert.Null(Near(new Neighbour("NEW SHOP", "SUPER SHOP", 0.9), new Neighbour("NEW SHOP", "BISTRO", 0.9)));
        // Another pattern's neighbours, or the other direction's lines, never count.
        Assert.Null(Near(new Neighbour("OTHER", "FOOD SHOP", 0.99)));
        Assert.Equal(Groceries, Near(new Neighbour("NEW SHOP", "FOOD SHOP", 0.9))!.Category);
    }

    private static Pick? Named(params CategoryNeighbour[] near) => ByName("NEW SHOP", true, near, 0.7);

    [Fact]
    public void With_no_history_the_nearest_category_name_well_ahead_of_the_second_decides()
    {
        var pick = Named(new CategoryNeighbour("NEW SHOP", true, Groceries, 0.754), new CategoryNeighbour("NEW SHOP", true, Restaurants, 0.73))!;
        Assert.Equal((Groceries, 0, SuggestionBases.CategoryName, 75), (pick.Category, pick.Lines, pick.Basis, pick.Agreement));
        // Under the threshold, or too close to the second: nothing.
        Assert.Null(Named(new CategoryNeighbour("NEW SHOP", true, Groceries, 0.69)));
        Assert.Null(Named(new CategoryNeighbour("NEW SHOP", true, Groceries, 0.75), new CategoryNeighbour("NEW SHOP", true, Restaurants, 0.74)));
        // Another pattern's or the other side's categories do not count, neither to pick nor to compare.
        Assert.Null(Named(new CategoryNeighbour("OTHER", true, Groceries, 0.9), new CategoryNeighbour("NEW SHOP", false, Groceries, 0.9)));
        Assert.Equal(Groceries, Named(new CategoryNeighbour("NEW SHOP", true, Groceries, 0.75), new CategoryNeighbour("NEW SHOP", false, Restaurants, 0.749))!.Category);
        Assert.Equal(("Alimentation › Courses", "Divers"), (OptionText("Alimentation", "Courses"), OptionText(null, "Divers")));
    }

    [Fact]
    public void A_household_name_is_held_back_as_labels_write_it()
        => Assert.Equal(["ELODIE", "MARTIN", "LEA"], NameWords(["Élodie Martin", "Léa", "Jo"]));
}
