using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;

namespace Nid.Subscriptions.Tests;

public class ListGroupingTests
{
    private static ProviderSummaryView Provider(string key, params string[] labels)
        => new(key, key.ToUpperInvariant(), "custom", labels.Select(l => new LabelView(l, l, "#000")).ToList(), null);

    private static Guid Person(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static SubscriptionSummary Sub(
        string? monthly, ProviderSummaryView? provider = null, bool estimate = false, string? annual = null,
        params (Guid Person, int Bp)[] owners)
        => new(new ItemId(Guid.NewGuid()), "s", provider, true, null, null, null, [], [], null,
            monthly is null ? null : new MoneyView(monthly, "EUR"), estimate, 0, [],
            (owners.Length == 0 ? [(Person(1), 10_000)] : owners).Select(o => new ItemOwnerView(o.Person, o.Bp)).ToList(),
            annual is null ? null : new MoneyView(annual, "EUR"));

    [Fact]
    public void Two_subscriptions_of_one_provider_make_one_group()
    {
        var anthropic = Provider("anthropic");
        var items = new[] { Sub("18.35", anthropic), Sub("18.00", anthropic) };

        var group = Assert.Single(ListGrouping.Group(ListGroup.Provider, items));

        Assert.Equal("anthropic", group.Key);
        Assert.Equal("ANTHROPIC", group.Name);
        Assert.Equal(2, group.ItemIds.Count);
        Assert.Equal(new MoneyView("36.35", "EUR"), group.AmortizedCostConverted);
    }

    [Fact]
    public void A_subscription_without_provider_goes_to_none()
    {
        var group = Assert.Single(ListGrouping.Group(ListGroup.Provider, [Sub("5")]));

        Assert.Equal(ListGrouping.NoneKey, group.Key);
        Assert.Null(group.Name);
    }

    [Fact]
    public void Label_groups_use_the_first_provider_label()
    {
        var group = Assert.Single(ListGrouping.Group(ListGroup.Label, [Sub("5", Provider("p", "shopping", "streaming")), Sub("3", Provider("q", "shopping"))]));

        Assert.Equal("shopping", group.Key);
        Assert.Equal(2, group.ItemIds.Count);
        Assert.Equal("8", group.AmortizedCostConverted!.Amount);
    }

    [Fact]
    public void A_provider_without_label_goes_to_none_for_labels()
        => Assert.Equal(ListGrouping.NoneKey, Assert.Single(ListGrouping.Group(ListGroup.Label, [Sub("5", Provider("p"))])).Key);

    [Fact]
    public void A_shared_subscription_counts_its_share_under_each_owner()
    {
        var sub = Sub("64.09", annual: "769.08", owners: [(Person(1), 5000), (Person(2), 5000)]);

        var groups = ListGrouping.Group(ListGroup.Person, [sub]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g =>
        {
            Assert.Null(g.Name);
            Assert.Equal("32.045", g.AmortizedCostConverted!.Amount);
            Assert.Equal("384.54", g.AnnualCostConverted!.Amount);
        });
    }

    [Fact]
    public void A_person_group_item_carries_the_persons_share()
    {
        var sub = Sub("64.09", annual: "769.08", owners: [(Person(1), 5000), (Person(2), 5000)]);

        Assert.All(ListGrouping.Group(ListGroup.Person, [sub]), g =>
        {
            var item = Assert.Single(g.Items);
            Assert.Equal(sub.Id.Value, item.Id);
            Assert.Equal("32.045", item.AmortizedCostConverted!.Amount);
            Assert.Equal("384.54", item.AnnualCostConverted!.Amount);
        });
    }

    [Fact]
    public void A_provider_group_item_carries_the_subscriptions_figures()
    {
        var sub = Sub("64.09", Provider("p"), annual: "769.08");

        var item = Assert.Single(Assert.Single(ListGrouping.Group(ListGroup.Provider, [sub])).Items);

        Assert.Equal(new GroupItemView(sub.Id.Value, sub.AmortizedCostConverted, sub.AnnualCostConverted), item);
    }

    [Fact]
    public void A_null_child_makes_the_group_total_null()
    {
        var p = Provider("p");

        var group = Assert.Single(ListGrouping.Group(ListGroup.Provider, [Sub("5", p), Sub(null, p)]));

        Assert.Null(group.AmortizedCostConverted);
    }

    [Fact]
    public void An_estimated_child_makes_the_group_an_estimate()
    {
        var p = Provider("p");

        Assert.True(Assert.Single(ListGrouping.Group(ListGroup.Provider, [Sub("5", p), Sub("1", p, estimate: true)])).ConvertedIsEstimate);
    }

    [Fact]
    public void Groups_sort_by_monthly_cost_descending_then_name()
    {
        var groups = ListGrouping.Group(ListGroup.Provider,
            [Sub("5", Provider("b")), Sub("5", Provider("a")), Sub("9", Provider("c")), Sub(null, Provider("d"))]);

        Assert.Equal(["c", "a", "b", "d"], groups.Select(g => g.Key));
    }

    [Fact]
    public void None_returns_no_groups()
        => Assert.Empty(ListGrouping.Group(ListGroup.None, [Sub("5", Provider("a"))]));
}
