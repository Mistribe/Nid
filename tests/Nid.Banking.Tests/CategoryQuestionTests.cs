using Nid.Banking.Application;

namespace Nid.Banking.Tests;

/// <summary>#821: a household's categories of a side as the two steps ask them.</summary>
public class CategoryQuestionTests
{
    [Fact]
    public void A_child_whose_parent_is_out_of_use_is_a_top_and_a_side_too_large_for_a_list_is_not_asked()
    {
        Guid food = Guid.NewGuid(), groceries = Guid.NewGuid(), orphan = Guid.NewGuid();

        var tree = CategoryQuestion.Tree([(groceries, food, "Courses"), (food, null, "Alimentation"), (orphan, Guid.NewGuid(), "Cantine")])!;

        Assert.Equal([("Alimentation", 1), ("Cantine", 0)], tree.Select(t => (t.Name, t.Children.Count)));
        // 25 tops and « none of these » make 26; one more would need a lower-case id.
        Assert.NotNull(CategoryQuestion.Tree([.. Enumerable.Range(0, 25).Select(i => (Guid.NewGuid(), (Guid?)null, $"Top {i}"))]));
        Assert.Null(CategoryQuestion.Tree([.. Enumerable.Range(0, 26).Select(i => (Guid.NewGuid(), (Guid?)null, $"Top {i}"))]));
        Assert.Null(CategoryQuestion.Tree([(food, null, "Alimentation"), .. Enumerable.Range(0, 27).Select(i => (Guid.NewGuid(), (Guid?)food, $"Child {i}"))]));
    }

    [Fact]
    public void A_top_reads_with_its_children_names_within_an_option_length()
    {
        var food = Guid.NewGuid();
        var top = CategoryQuestion.Tree([(food, null, "Alimentation"), .. Enumerable.Range(0, 20).Select(i => (Guid.NewGuid(), (Guid?)food, $"Sous-catégorie {i:00}"))])!
            .Single();

        var text = CategoryQuestion.TopText(top);

        Assert.StartsWith("Alimentation (Sous-catégorie 00, Sous-catégorie 01", text, StringComparison.Ordinal);
        Assert.Equal(200, text.Length);
    }
}
