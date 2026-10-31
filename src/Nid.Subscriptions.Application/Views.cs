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

/// <param name="AnnualCostConverted">
/// The 12 months from the 1st of the current month, converted; charges past the walk horizon (<c>?horizon=</c>) are not
/// projected, so a horizon under 12 months understates it.
/// </param>
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
    bool BilledMonthly,
    int FreeDays,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyList<ItemOwnerView> Owners,
    MoneyView? AnnualCostConverted);

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
    IReadOnlyList<GroupView> Groups,
    IReadOnlyList<FindingView> Findings,
    MoneyView? HouseholdAnnualCostConverted) : IExposesItems
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
    bool BilledMonthly,
    int FreeDays,
    IReadOnlyList<CoverageView> Coverage,
    IReadOnlyList<ChargeView> Charges,
    IReadOnlyList<FindingView> Findings,
    string Visibility,
    IReadOnlyList<OwnerView> Owners,
    IReadOnlyList<Guid> Users,
    string? Note,
    IReadOnlyList<PhaseView> Phases,
    IReadOnlyList<PurchaseView> Purchases,
    bool CanWrite,
    ContractTermsView? ContractTerms,
    CancellationView Cancellation,
    InsuranceView? Insurance = null) : IExposesItems
{
    // Every item the detail names: the output guard then proves each grant target and the insured property are the reader's to see.
    public IEnumerable<ItemId> ExposedItems() => [Id, ..Purchases.SelectMany(p => p.Grants).Select(g => new ItemId(g.SubscriptionId)),
        ..Insurance?.Property is { } property ? [new ItemId(property.Id)] : Array.Empty<ItemId>()];
}

public sealed record OwnerView(Guid PersonId, int ShareBp);
public sealed record DurationView(int Count, string Unit)
{
    public static DurationView From(Core.Contracts.Duration d) => new(d.Count, d.Unit.ToString().ToLowerInvariant());
}
public sealed record PayerView(string Type, Guid? PersonId)
{
    public static PayerView From(Payer p) => new(p.Type.ToString().ToLowerInvariant(), p.PersonId?.Value);
}
public sealed record PhaseView(Guid Id, LocalDate StartsOn, LocalDate? EndsOn, MoneyView Price, DurationView Every, DurationView Covers,
    int? MaxCycles, PayerView Payer, string? Note);
public sealed record GrantView(Guid SubscriptionId, DurationView Duration, string Source, LocalDate? StartsOn);
public sealed record PurchaseView(Guid Id, LocalDate Date, string Label, MoneyView Price, PayerView Payer, string Kind,
    string? AllocatedAmount, IReadOnlyList<GrantView> Grants, string? Note);

/// <summary>1E2a design §2.4: what the detail page needs to edit, already filtered for the reader.</summary>
public sealed record SubscriptionEditing(
    string Visibility, IReadOnlyList<OwnerView> Owners, IReadOnlyList<Guid> Users, string? Note,
    IReadOnlyList<PhaseView> Phases, IReadOnlyList<PurchaseView> Purchases, bool CanWrite);

/// <summary>What the household typed about the contract (design 2026-10-30-contract-terms-insurance §2).</summary>
public sealed record ContractTermsView(LocalDate? AnniversaryOn, DurationView? Notice, LocalDate? CommitmentEndsOn, LocalDate? CancellableFrom)
{
    public static ContractTermsView? From(ContractTerms? terms)
        => terms is not { IsEmpty: false }
            ? null
            : new(terms.AnniversaryOn, terms.Notice is { } notice ? DurationView.From(notice) : null, terms.CommitmentEndsOn, terms.CancellableFrom);
}

/// <summary>The property an insurance covers, only when its reader sees it: otherwise the view has none (spec §8).</summary>
public sealed record InsuredPropertyView(Guid Id, string Name);

public sealed record GuaranteeView(string Label, string? Ceiling, string? Deductible);

/// <summary>What an insurance subscription covers (design 2026-10-30-contract-terms-insurance §7 and §10).</summary>
public sealed record InsuranceView(
    string Kind, InsuredPropertyView? Property, string? InsuredLabel, string? PolicyNumber, string? InsurerContact,
    IReadOnlyList<GuaranteeView> Guarantees)
{
    public static InsuranceView From(InsuranceDetails details, InsuredPropertyView? property) => new(
        details.Kind.ToString().ToLowerInvariant(), property, details.InsuredLabel, details.PolicyNumber, details.InsurerContact,
        details.Guarantees.Select(g => new GuaranteeView(g.Label, g.Ceiling, g.Deductible)).ToList());
}

/// <summary>The server's answer to "until when can this be left?". <paramref name="Status"/> is none, deadline, free_from or free_now.</summary>
public sealed record CancellationView(string Status, LocalDate? LastDayToCancel, LocalDate? EffectiveOn, LocalDate? FreeOn)
{
    public static CancellationView From(CancellationOutlook outlook) => new(
        outlook.Status switch
        {
            CancellationStatus.Deadline => "deadline",
            CancellationStatus.FreeFrom => "free_from",
            CancellationStatus.FreeNow => "free_now",
            _ => "none",
        },
        outlook.LastDayToCancel, outlook.EffectiveOn, outlook.FreeOn);
}
