using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;

namespace Nid.Banking.Tests;

public class CategoriesTests
{
    [Fact]
    public void Seed_has_unique_keys_and_ids_and_children_share_their_parents_kind()
    {
        var all = CategorySeed.All.ToList();
        Assert.Equal(all.Count, all.Select(c => c.Key).Distinct().Count());
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        foreach (var child in all.Where(c => c.ParentKey is not null))
        {
            var parent = CategorySeed.Find(child.ParentKey!);
            Assert.NotNull(parent);
            Assert.Null(parent.ParentKey);
            Assert.Equal(parent.Kind, child.Kind);
            Assert.StartsWith(parent.Key + ".", child.Key);
            Assert.True(all.IndexOf(parent) < all.IndexOf(child));
        }
    }

    [Fact]
    public void Seed_pins_exact_entries_and_count()
    {
        Assert.Equal(47, CategorySeed.All.Count);
        var groceries = CategorySeed.Find("food.groceries")!;
        Assert.Equal(("Courses", CategoryKind.Expense, "food"), (groceries.Name, groceries.Kind, groceries.ParentKey));
        var card = CategorySeed.Find("internal.card")!;
        Assert.Equal(("Règlement carte", CategoryKind.Transfer, "internal"), (card.Name, card.Kind, card.ParentKey));
        var interest = CategorySeed.Find("income.interest")!;
        Assert.Equal(("Intérêts", CategoryKind.Income, "income"), (interest.Name, interest.Kind, interest.ParentKey));
        var rent = CategorySeed.Find("income.rent")!;
        Assert.Equal(("Loyers perçus", CategoryKind.Income, "income"), (rent.Name, rent.Kind, rent.ParentKey));
    }

    [Fact]
    public void Seed_names_are_unique_among_siblings()
        => Assert.All(
            CategorySeed.All.GroupBy(c => c.ParentKey),
            g => Assert.Equal(g.Count(), g.Select(c => c.Name.ToUpperInvariant()).Distinct().Count()));

    [Theory]
    [InlineData(null, CategorySource.Hint, false, true)]
    [InlineData(CategorySource.Hint, CategorySource.Rule, false, true)]
    [InlineData(CategorySource.Rule, CategorySource.Hint, false, false)]
    [InlineData(CategorySource.Rule, CategorySource.Link, false, true)]
    [InlineData(CategorySource.Link, CategorySource.Rule, false, false)]
    [InlineData(CategorySource.Link, CategorySource.Link, false, true)]
    [InlineData(CategorySource.Hand, CategorySource.Link, false, false)]
    [InlineData(CategorySource.Assistant, CategorySource.Rule, false, false)]
    [InlineData(CategorySource.Hand, CategorySource.Hand, true, true)]
    [InlineData(CategorySource.Assistant, CategorySource.Hand, true, true)]
    [InlineData(CategorySource.Hand, CategorySource.Assistant, true, true)]
    [InlineData(CategorySource.Hand, CategorySource.Hand, false, false)]
    [InlineData(CategorySource.Assistant, CategorySource.Assistant, false, false)]
    public void CanReplace_follows_the_precedence(CategorySource? current, CategorySource incoming, bool byPerson, bool expected)
        => Assert.Equal(expected, CategoryPrecedence.CanReplace(current, incoming, byPerson));

    [Theory]
    [InlineData(LinkOutcome.InternalTransfer, null, null, null, false, "", "internal.transfer")]
    [InlineData(LinkOutcome.CardSettlement, null, null, null, false, "", "internal.card")]
    [InlineData(LinkOutcome.Contribution, null, null, null, false, "", "internal.savings")]
    [InlineData(LinkOutcome.AsExpected, ChargeKinds.LoanInstalment, null, null, false, "", "home.rent_loan")]
    [InlineData(LinkOutcome.AsExpected, ChargeKinds.PropertyCost, null, null, false, "", "home.utilities")]
    [InlineData(LinkOutcome.Income, ChargeKinds.Rent, null, null, true, "", "income.rent")]
    [InlineData(LinkOutcome.AsExpected, ChargeKinds.Childcare, null, null, false, "", "children.childcare")]
    [InlineData(LinkOutcome.AsExpected, ChargeKinds.Subscription, null, null, false, "streaming", "leisure.subscriptions")]
    [InlineData(LinkOutcome.PriceDiffers, ChargeKinds.Subscription, null, null, false, "telecom", "home.telecom")]
    [InlineData(LinkOutcome.AsExpected, ChargeKinds.Subscription, null, null, false, "unknown,insurance", "insurance")]
    [InlineData(LinkOutcome.AsExpected, ChargeKinds.Subscription, null, null, false, "", "leisure.subscriptions")]
    [InlineData(LinkOutcome.AsExpected, null, "realestate", "loan", false, "", "home.rent_loan")]
    [InlineData(LinkOutcome.AsExpected, null, "realestate", "property", false, "", "home.utilities")]
    [InlineData(LinkOutcome.Income, null, "realestate", "property", true, "", "income.rent")]
    [InlineData(LinkOutcome.AsExpected, null, "childcare", "x", false, "", "children.childcare")]
    [InlineData(LinkOutcome.AsExpected, null, "subscriptions", "x", false, "music", "leisure.subscriptions")]
    [InlineData(LinkOutcome.AsExpected, null, "other", "x", false, "", null)]
    public void KeyFor_maps_a_link_to_a_category(
        LinkOutcome outcome, string? charge, string? module, string? itemKind, bool credit, string labels, string? expected)
        => Assert.Equal(
            expected,
            LinkCategory.KeyFor(outcome, charge, module, itemKind, credit, labels.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Subscription_labels_only_point_to_seeded_categories()
        => Assert.All(LinkCategory.SubscriptionLabels.Values, k => Assert.NotNull(CategorySeed.Find(k)));
}
