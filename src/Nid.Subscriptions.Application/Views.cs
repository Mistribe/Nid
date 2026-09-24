using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>
/// An amount as the API sends it: a decimal string in invariant culture, with the scale the engine produced
/// ("17.20", not 17.2). The browser displays it and never computes with it (frontend/README.md §2).
/// </summary>
public sealed record MoneyView(string Amount, string Currency)
{
    public static MoneyView From(Money money) => new(money.AmountText, money.Currency.Code);

    public static IReadOnlyList<MoneyView> From(CostBreakdown breakdown)
        => breakdown.Totals.Select(From).ToList();
}

public sealed record FindingView(string Code, string Message, string? Reference)
{
    public static FindingView From(Finding finding) => new(finding.Code, finding.Message, finding.Reference);
}

public sealed record CoverageView(LocalDate Start, LocalDate EndBefore, string Source, bool IsProjected);

public sealed record ChargeView(LocalDate Date, MoneyView Price, bool IsProjected, Guid? PurchaseId);

public sealed record SubscriptionSummary(
    ItemId Id,
    string DisplayName,
    ProviderSummaryView? Provider,
    bool AutoRenew,
    LocalDate? ExpiresOn,
    LocalDate? NextChargeOn,
    MoneyView? NextChargeAmount,
    IReadOnlyList<MoneyView> CashCost,
    IReadOnlyList<MoneyView> AmortizedCost,
    MoneyView? CashCostConverted,
    MoneyView? AmortizedCostConverted,
    bool ConvertedIsEstimate,
    int FreeDays,
    IReadOnlyList<FindingView> Findings);

/// <summary>
/// The household totals are NOT the sum of the rows: a purchase granting to several subscriptions shows in
/// full on each of them but left the account once, so it is counted once here. <paramref name="SharedPurchaseIds"/>
/// names those purchases so a caller can explain the difference instead of looking wrong, and
/// <paramref name="Findings"/> says so in the machine-readable form the rest of the module uses: the cash
/// total deduplicates such a purchase while the amortized one does not, so the two can disagree on the same
/// response (<c>subscriptions.amortized_double_count</c>).
/// The converted totals are in the household's reference currency, each charge at the rate of its date; null
/// when any rate is missing, with a <c>core.fx_rate_missing</c> finding naming it (1C2a design §5).
/// </summary>
public sealed record SubscriptionList(
    IReadOnlyList<SubscriptionSummary> Items,
    IReadOnlyList<MoneyView> HouseholdCashCost,
    IReadOnlyList<MoneyView> HouseholdAmortizedCost,
    MoneyView? HouseholdCashCostConverted,
    MoneyView? HouseholdAmortizedCostConverted,
    bool HouseholdConvertedIsEstimate,
    IReadOnlyList<Guid> SharedPurchaseIds,
    IReadOnlyList<FindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

/// <summary>One subscription as its detail page reads it, with its provider (design §5 "Views"): null when it has none.</summary>
public sealed record SubscriptionDetail(
    ItemId Id,
    string DisplayName,
    ProviderSummaryView? Provider,
    bool AutoRenew,
    LocalDate? CancelledAt,
    LocalDate? ExpiresOn,
    LocalDate? NextChargeOn,
    MoneyView? NextChargeAmount,
    IReadOnlyList<MoneyView> CashCost,
    IReadOnlyList<MoneyView> AmortizedCost,
    MoneyView? CashCostConverted,
    MoneyView? AmortizedCostConverted,
    bool ConvertedIsEstimate,
    int FreeDays,
    IReadOnlyList<CoverageView> Coverage,
    IReadOnlyList<ChargeView> Charges,
    IReadOnlyList<FindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}
