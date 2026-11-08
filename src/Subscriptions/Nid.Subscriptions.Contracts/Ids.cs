namespace Nid.Subscriptions.Contracts;

public readonly record struct BillingPhaseId(Guid Value)
{
    public static BillingPhaseId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct PurchaseId(Guid Value)
{
    public static PurchaseId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
