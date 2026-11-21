using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>#427: the budgets proposed from the Mois habituel.</summary>
public class BudgetSuggestionsTests
{
    private static readonly StringComparer ByName = StringComparer.Ordinal;

    private static BankCategory Category(string name, Guid? parent = null, CategoryKind kind = CategoryKind.Expense, bool archived = false)
        => new(Guid.NewGuid(), parent, null, name, kind, archived ? Instant.FromUtc(2026, 1, 1, 0, 0) : null, null);

    [Theory]
    [InlineData("123.40", "130")]
    [InlineData("120", "120")]
    [InlineData("120.00", "120")]
    [InlineData("0.01", "10")]
    [InlineData("9.99", "10")]
    [InlineData("10.01", "20")]
    public void The_proposal_is_the_usual_month_rounded_up_to_the_next_10(string average, string proposed)
        => Assert.Equal(decimal.Parse(proposed), BudgetSuggestions.Proposed(decimal.Parse(average, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Only_branches_without_a_budget_most_spent_first_children_included()
    {
        var food = Category("Alimentation");
        var groceries = Category("Courses", food.Id);
        var home = Category("Logement");
        var transport = Category("Transport");
        var fuel = Category("Carburant", transport.Id);
        var leisure = Category("Loisirs");
        var old = Category("Ancienne", leisure.Id, archived: true);
        var gone = Category("Archivée", archived: true);
        var salary = Category("Salaires", kind: CategoryKind.Income);
        var quiet = Category("Santé");
        var usual = new Dictionary<Guid, decimal>
        {
            [food.Id] = 10m, [groceries.Id] = 300.404m, [home.Id] = 900m, [fuel.Id] = 80m, [leisure.Id] = 50m, [gone.Id] = 70m, [salary.Id] = 2_000m,
        };

        // Logement budgeted itself, Transport through a child; an archived child's budget never blocks Loisirs.
        var suggested = BudgetSuggestions.For([food, groceries, home, transport, fuel, leisure, old, gone, salary, quiet],
            new HashSet<Guid> { home.Id, fuel.Id, old.Id }, usual, new HashSet<Guid> { leisure.Id }, ByName);

        Assert.Equal([("Alimentation", 310.40m, 320m, false), ("Loisirs", 50m, 50m, true)],
            suggested.Select(s => (s.Name, s.Average, s.Proposed, s.Committed)));
    }

    [Fact]
    public void The_eight_biggest_are_preselected_ties_by_name()
    {
        var categories = Enumerable.Range(1, 10).Select(i => Category($"C{i:00}")).ToList();
        // C01 spent least; C02 and C03 tie, by name.
        var usual = categories.ToDictionary(c => c.Id, c => c.Name switch { "C01" => 200m, "C03" => 298m, _ => 300m - int.Parse(c.Name[1..]) });

        var suggested = BudgetSuggestions.For(categories, new HashSet<Guid>(), usual, new HashSet<Guid>(), ByName);

        Assert.Equal(["C02", "C03", "C04", "C05", "C06", "C07", "C08", "C09", "C10", "C01"], suggested.Select(s => s.Name));
        Assert.Equal(8, suggested.Count(s => s.Preselected));
        Assert.All(suggested.Take(8), s => Assert.True(s.Preselected));
    }
}
