using NodaTime;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>Keyed by the Core item it belongs to: name, owners and visibility live in the Core, not here.</summary>
public sealed class SubscriptionRow
{
    public Guid ItemId { get; set; }
    public string? ProviderKey { get; set; }
    public bool AutoRenew { get; set; } = true;
    public LocalDate? CancelledAt { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public uint Version { get; set; }
    public List<SubscriptionUserRow> Users { get; set; } = [];
    public List<BillingPhaseRow> Phases { get; set; } = [];
}

public sealed class SubscriptionUserRow
{
    public Guid ItemId { get; set; }
    public Guid PersonId { get; set; }
}

public sealed class BillingPhaseRow
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public LocalDate StartsOn { get; set; }
    public LocalDate? EndsOn { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = "EUR";
    public int EveryCount { get; set; }
    public string EveryUnit { get; set; } = "month";
    public int? CoversCount { get; set; }
    public string? CoversUnit { get; set; }
    public int? MaxCycles { get; set; }
    public string PayerType { get; set; } = "household";
    public Guid? PayerPersonId { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
}

public sealed class PurchaseRow
{
    public Guid Id { get; set; }
    public LocalDate Date { get; set; }
    public string Label { get; set; } = "";
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = "EUR";
    public string PayerType { get; set; } = "household";
    public Guid? PayerPersonId { get; set; }
    public string Kind { get; set; } = "prepaid_term";
    public decimal? AllocatedAmount { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public List<GrantRow> Grants { get; set; } = [];
}

/// <summary>
/// No foreign key to <c>subscription</c>: a purchase may grant to a subscription the household has not
/// created yet, or to one outside this module. The engine filters grants it does not own.
/// </summary>
public sealed class GrantRow
{
    public Guid PurchaseId { get; set; }
    public int Index { get; set; }
    public Guid SubscriptionItemId { get; set; }
    public int DurationCount { get; set; }
    public string DurationUnit { get; set; } = "month";
    public string Source { get; set; } = "paid";
    public LocalDate? StartsOn { get; set; }
}
