namespace Nid.TestSupport;

/// <summary>What <c>/api/v1/subscriptions</c> answers, as the read and write tests deserialise it.</summary>
public static class SubscriptionApi
{
    public sealed record MoneyResponse(string Amount, string Currency);
    public sealed record FindingResponse(string Code, string Message, string? Reference);
    public sealed record SummaryResponse(Guid Id, string DisplayName, IReadOnlyList<MoneyResponse> CashCost);
    public sealed record ListResponse(
        IReadOnlyList<SummaryResponse> Items,
        IReadOnlyList<MoneyResponse> HouseholdCashCost,
        IReadOnlyList<Guid> SharedPurchaseIds);
    public sealed record CoverageResponse(string Start, string EndBefore, string Source, bool IsProjected);
    public sealed record ChargeResponse(string Date, MoneyResponse Price, bool IsProjected, Guid? PurchaseId);
    public sealed record DetailResponse(
        Guid Id, string DisplayName, string? NextChargeOn, MoneyResponse? NextChargeAmount,
        IReadOnlyList<CoverageResponse> Coverage, IReadOnlyList<ChargeResponse> Charges,
        IReadOnlyList<FindingResponse> Findings);
}
