using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using NodaTime;

namespace Nid.Tax.Application;

/// <summary>
/// Sale tax spec §5: an item's page (<c>Total</c> false, one item, no totals) or the scope's total « Si tout était vendu ». Amounts are
/// in <c>Currency</c>; <c>Notes</c> are codes the web translates (<c>total_not_sum_of_pages</c>, <c>life_insurance_visible_contracts_only</c>).
/// </summary>
public sealed record SaleTaxView(
    string Currency, int RulesYear, bool CarriedForward, bool Total, IReadOnlyList<SaleItemView> Items, SaleTotalsView? Totals,
    IReadOnlyList<string> Notes) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Item);
}

/// <summary>The sums over the items that have a net.</summary>
public sealed record SaleTotalsView(string Value, string IncomeTax, string SocialTax, string Surtax, string Net);
