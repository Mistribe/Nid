using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;

namespace Nid.Subscriptions.Application;

public sealed record MoneyView(decimal Amount, string Currency)
{
    public static MoneyView From(Money money) => new(money.Amount, money.Currency.Code);

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
    bool AutoRenew,
    LocalDate? ExpiresOn,
    LocalDate? NextChargeOn,
    MoneyView? NextChargeAmount,
    IReadOnlyList<MoneyView> CashCost,
    IReadOnlyList<MoneyView> AmortizedCost,
    int FreeDays,
    IReadOnlyList<FindingView> Findings);

/// <summary>
/// The household totals are NOT the sum of the rows: a purchase granting to several subscriptions shows in
/// full on each of them but left the account once, so it is counted once here. <paramref name="SharedPurchaseIds"/>
/// names those purchases so a caller can explain the difference instead of looking wrong.
/// </summary>
public sealed record SubscriptionList(
    IReadOnlyList<SubscriptionSummary> Items,
    IReadOnlyList<MoneyView> HouseholdCashCost,
    IReadOnlyList<MoneyView> HouseholdAmortizedCost,
    IReadOnlyList<Guid> SharedPurchaseIds) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

/// <summary>
/// No `ProviderKey` here, deliberately: the 1B engine input carries only what it computes with, so the store
/// writes the provider key but has nowhere to read it back into. It returns with the catalog in 1C2, which is
/// what gives it meaning (name, icon, labels). A field that is always null is worse than an absent one.
/// </summary>
public sealed record SubscriptionDetail(
    ItemId Id,
    string DisplayName,
    bool AutoRenew,
    LocalDate? CancelledAt,
    LocalDate? ExpiresOn,
    LocalDate? NextChargeOn,
    MoneyView? NextChargeAmount,
    IReadOnlyList<MoneyView> CashCost,
    IReadOnlyList<MoneyView> AmortizedCost,
    int FreeDays,
    IReadOnlyList<CoverageView> Coverage,
    IReadOnlyList<ChargeView> Charges,
    IReadOnlyList<FindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}
