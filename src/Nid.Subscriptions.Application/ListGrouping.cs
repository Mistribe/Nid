using System.Globalization;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.Application;

public enum ListGroup { None, Provider, Label, Person }

/// <summary>
/// A group of the list with its server subtotals, in the reference currency; <paramref name="SpentTotal"/> (#395) counts a
/// subscription without any charge yet as nothing, and is partial when one of its members is. <paramref name="Name"/> is the provider's or the
/// label's; null for a person (the screen already has the names) and for <see cref="ListGrouping.NoneKey"/>.
/// <paramref name="Items"/> follows <paramref name="ItemIds"/>, with each subscription's figures as the group counts them.
/// </summary>
public sealed record GroupView(
    string Key, string? Name, IReadOnlyList<Guid> ItemIds, IReadOnlyList<GroupItemView> Items,
    MoneyView? AmortizedCostConverted, MoneyView? AnnualCostConverted, bool ConvertedIsEstimate,
    MoneyView? SpentTotal = null, bool SpentPartial = false);

/// <summary>A subscription inside a group: its full converted figures, or the person's share of them under a person group.</summary>
public sealed record GroupItemView(Guid Id, MoneyView? AmortizedCostConverted, MoneyView? AnnualCostConverted, MoneyView? SpentTotal = null);

public static class ListGrouping
{
    public const string NoneKey = "none";

    /// <param name="spentOf">A whole group's spent total from its members' distinct charges (<see cref="SpendingHistory.SpentOf"/>);
    /// without it, or under a person group (shares), the members' totals are summed.</param>
    public static IReadOnlyList<GroupView> Group(ListGroup by, IReadOnlyList<SubscriptionSummary> items,
        Func<IReadOnlyList<ItemId>, (MoneyView? Total, bool Partial)>? spentOf = null)
    {
        if (by == ListGroup.None) return [];

        // (item, share in basis points) per group: only a person group splits an item.
        var buckets = new Dictionary<string, (string? Name, List<(SubscriptionSummary Item, int Bp)> Members)>();
        void Add(string key, string? name, SubscriptionSummary item, int bp)
        {
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = (name, []);
            bucket.Members.Add((item, bp));
        }

        foreach (var item in items)
        {
            switch (by)
            {
                case ListGroup.Provider:
                    if (item.Provider is { } p) Add(p.Key, p.Name, item, 10_000); else Add(NoneKey, null, item, 10_000);
                    break;
                case ListGroup.Label:
                    if (item.Provider?.Labels.FirstOrDefault() is { } l) Add(l.Key, l.Name, item, 10_000); else Add(NoneKey, null, item, 10_000);
                    break;
                default:
                    foreach (var owner in item.Owners) Add(owner.PersonId.ToString(), null, item, owner.ShareBp);
                    break;
            }
        }

        return buckets
            .Select(b => (Key: b.Key, b.Value.Name, Group: b.Value.Members))
            .Select(b =>
            {
                var monthly = Sum(b.Group, i => i.AmortizedCostConverted);
                var annual = Sum(b.Group, i => i.AnnualCostConverted);
                // An item appears once per group (an owner holds one share), so Items and ItemIds line up.
                var members = b.Group.DistinctBy(m => m.Item.Id).ToList();
                var spent = spentOf is not null && by != ListGroup.Person
                    ? spentOf(members.Select(m => m.Item.Id).ToList())
                    : (Total: Sum(b.Group.Where(m => m.Item.Spent?.Total is not null).ToList(), i => i.Spent!.Total)?.View,
                        Partial: b.Group.Any(m => m.Item.Spent?.Partial == true));
                return (b.Key, b.Name, Monthly: monthly, View: new GroupView(
                    b.Key, b.Name, members.Select(m => m.Item.Id.Value).ToList(),
                    members.Select(m => m.Bp == 10_000
                        ? new GroupItemView(m.Item.Id.Value, m.Item.AmortizedCostConverted, m.Item.AnnualCostConverted, m.Item.Spent?.Total)
                        : new GroupItemView(m.Item.Id.Value,
                            Sum([m], i => i.AmortizedCostConverted)?.View, Sum([m], i => i.AnnualCostConverted)?.View,
                            Sum([m], i => i.Spent?.Total)?.View)).ToList(),
                    monthly?.View, annual?.View, b.Group.Any(m => m.Item.ConvertedIsEstimate),
                    spent.Total, spent.Partial));
            })
            .OrderByDescending(g => g.Monthly is not null).ThenByDescending(g => g.Monthly?.Total ?? 0m)
            .ThenBy(g => g.Name ?? g.Key, StringComparer.Ordinal)
            .Select(g => g.View)
            .ToList();
    }

    // Σ amount × share / 10 000 over the children, unrounded; null when any child's amount is null.
    private static (decimal Total, MoneyView View)? Sum(
        List<(SubscriptionSummary Item, int Bp)> members, Func<SubscriptionSummary, MoneyView?> pick)
    {
        var total = 0m;
        string? currency = null;
        foreach (var (item, bp) in members)
        {
            if (pick(item) is not { } money) return null;
            total += decimal.Parse(money.Amount, CultureInfo.InvariantCulture) * bp / 10_000m;
            currency = money.Currency;
        }
        return currency is null ? null : (total, new MoneyView(total.ToString(CultureInfo.InvariantCulture), currency));
    }
}
