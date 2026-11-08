using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using NodaTime;

namespace Nid.Subscriptions.Application;

/// <summary>
/// The subscriptions overview (spec §4.1), every amount in the reference currency: null when a rate it needs is missing,
/// zero when there is nothing to count.
/// </summary>
public sealed record SubscriptionsOverview(
    string Currency, bool ConvertedIsEstimate,
    MoneyView? MonthlyAmortized, MoneyView? AnnualAmortized, MoneyView? MonthCash, MoneyView? MonthCashRemaining,
    int ActiveCount, int EndedCount, IReadOnlyList<MonthView> Months, IReadOnlyList<PersonShareView> ByPerson,
    IReadOnlyList<LabelShareView> ByLabel, IReadOnlyList<ChargeLineView> Upcoming, IReadOnlyList<WatchView> Watch,
    IReadOnlyList<FindingView> Findings, IReadOnlyList<YearView> Years, IReadOnlyList<TopProviderView> TopProviders,
    SavingsView Savings, PriceRisesView PriceRises) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Upcoming.Select(u => u.ItemId).Concat(Watch.SelectMany(w => w.ItemIds).Select(id => new ItemId(id)))
            .Concat(Savings.Items.Select(s => s.ItemId)).Concat(PriceRises.Items.Select(r => r.ItemId)).Distinct();
}

/// <summary>One month of cash out, <c>yyyy-mm</c>: charges of phases of a month or less, and the rest (longer phases, purchases).</summary>
public sealed record MonthView(string Month, MoneyView? Recurring, MoneyView? LongCycle);

public sealed record PersonShareView(Guid PersonId, MoneyView? Amortized, int OwnedAlone, int Shared);

/// <summary>A main label's share (D4); <paramref name="Name"/> and <paramref name="Color"/> are null for <see cref="ListGrouping.NoneKey"/>.</summary>
public sealed record LabelShareView(string Key, string? Name, string? Color, MoneyView? Amortized);

public sealed record ChargeLineView(
    LocalDate Date, ItemId ItemId, string DisplayName, ProviderSummaryView? Provider, MoneyView Price, MoneyView? Converted);

public sealed record WatchView(string Code, IReadOnlyList<Guid> ItemIds, LocalDate? Date, MoneyView? Amount);

public static class OverviewCodes
{
    public const string EndsWithoutRenewal = "subscriptions.ends_without_renewal";
    public const string LongCycleChargeSoon = "subscriptions.long_cycle_charge_soon";
    public const string SameProvider = "subscriptions.same_provider";
}
