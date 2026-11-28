using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;

namespace Nid.Banking.Tests;

/// <summary>#450: the evaluation set every model is calibrated on holds what a calibration needs to mean something.</summary>
public class CategoryEvalSetTests
{
    private static readonly CategoryEvalSet Set = CategoryEvalSet.Current;

    [Fact]
    public void It_holds_several_hundred_cases_about_one_in_seven_fitting_no_category()
    {
        Assert.Equal(2, Set.Version);
        Assert.True(Set.Cases.Count >= 400, $"{Set.Cases.Count} cases");
        Assert.True(Set.Cases.Count(c => c.Expected is null) >= 50);
        Assert.True(Set.History.Count >= 150, $"{Set.History.Count} history entries");
        Assert.All(Set.History, h => Assert.True(h.Lines >= 2, h.Label));
    }

    [Fact]
    public void Every_category_is_a_seed_category_of_its_side()
    {
        static void OfSide(string key, bool debit, string label)
        {
            var seed = CategorySeed.Find(key);
            Assert.True(seed is not null, $"{label}: {key}");
            Assert.True(seed!.Kind == (debit ? CategoryKind.Expense : CategoryKind.Income), $"{label}: {key} is not of its side");
        }

        foreach (var h in Set.History)
            OfSide(h.Category, h.Debit, h.Label);
        foreach (var c in Set.Cases.Where(c => c.Expected is not null))
            OfSide(c.Expected!, c.Debit, c.Label);
        // Every leaf of both sides is expected somewhere.
        var leaves = CategorySeed.All.Where(s => s.Kind != CategoryKind.Transfer && CategorySeed.All.All(o => o.ParentKey != s.Key)).Select(s => s.Key);
        Assert.Empty(leaves.Except(Set.Cases.Select(c => c.Expected).OfType<string>()));
    }

    [Fact]
    public void No_case_has_a_pattern_the_history_decides_on_its_own()
    {
        var history = Set.History.Select(h => CategoryEvalSet.Pattern(h.Label)).ToList();
        Assert.All(history, p => Assert.NotNull(p));
        var cases = Set.Cases.Select(c => (Pattern: CategoryEvalSet.Pattern(c.Label), c.Label)).ToList();
        Assert.All(cases, c => Assert.True(c.Pattern is not null, c.Label));
        Assert.Empty(cases.Where(c => history.Contains(c.Pattern)).Select(c => c.Label));
    }

    [Fact]
    public void The_options_are_every_seed_category_but_the_transfers_and_the_parents_as_a_model_reads_them()
    {
        var options = CategoryEvalSet.Options();
        Assert.Equal(CategorySeed.All.Count(s => s.Kind != CategoryKind.Transfer && CategorySeed.All.All(o => o.ParentKey != s.Key)), options.Count);
        Assert.Contains(options, o => o.Debit && o.Text == "Alimentation › Courses");
        Assert.Contains(options, o => o.Debit && o.Text == "Banque & frais");
        Assert.Contains(options, o => !o.Debit && o.Text == "Revenus › Salaire");
        Assert.DoesNotContain(options, o => o.Text == "Alimentation");
    }

    [Fact]
    public void A_worker_is_asked_each_side_in_two_lists_of_twenty_six_options_at_most()
    {
        var debit = CategoryEvalSet.Tree(true);
        Assert.Equal(CategorySeed.All.Count(s => s.Kind == CategoryKind.Expense && s.ParentKey is null), debit.Count);
        Assert.Equal("Alimentation (Courses, Livraison, Restaurants & cafés)", CategoryQuestion.TopText(debit[0]));
        Assert.Equal("Assurances", CategoryQuestion.TopText(debit[1]));
        var income = Assert.Single(CategoryEvalSet.Tree(false));
        Assert.Equal(("Revenus", 5), (income.Name, income.Children.Count));
        Assert.All(debit, t => Assert.True(t.Children.Count <= CategoryQuestion.MaxOptions));
    }
}
