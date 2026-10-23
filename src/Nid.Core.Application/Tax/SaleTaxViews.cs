using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Tax;

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

/// <summary>
/// One item sold today. <c>Status</c>: <c>taxed</c>, <c>exempt</c>, <c>loss</c>, <c>not_taxable</c>, <c>not_estimated</c>, or
/// <c>finding</c> with its <c>Finding</c> (<c>opening_date_unknown</c>, <c>other_currency</c>, <c>no_value</c>) and no figure.
/// <c>Surtax</c> is a property's; <c>Net</c> is null when not estimated.
/// </summary>
public sealed record SaleItemView(
    ItemId Item, string Module, string Name, string Kind, string Status, string? Finding, string? Value, LocalDate? ValueOn, bool Estimated,
    bool Stale, bool Approximate, int? YearsHeld, string? Gain, string? IncomeTax, string? SocialTax, string? Surtax, string? Net,
    PropertySaleView? Property, EnvelopeSaleView? Envelope, IReadOnlyList<string> Notes);

/// <summary>Methods are <c>actual</c>, <c>flat</c> or <c>none</c>; allowances in percent (<c>"8.25"</c>).</summary>
public sealed record PropertySaleView(
    string CostPrice, string FeesMethod, string WorksMethod, string IncomeAllowancePct, string SocialAllowancePct, IReadOnlyList<SaleOwnerView> Owners);

public sealed record SaleOwnerView(Guid Person, string Gain, string IncomeTax, string SocialTax, string Surtax);

/// <summary>A PEA's or a life insurance's remaining capital; the gain's three parts and the allowance used are a life insurance's.</summary>
public sealed record EnvelopeSaleView(string RemainingCapital, string? GainBefore2017, string? GainReduced, string? GainFull, string? AllowanceUsed);

/// <summary>The sums over the items that have a net.</summary>
public sealed record SaleTotalsView(string Value, string IncomeTax, string SocialTax, string Surtax, string Net);
