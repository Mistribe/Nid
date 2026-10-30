using NodaTime;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>Keyed by the Core item it belongs to: name, owners and visibility live in the Core, not here.</summary>
public sealed class SubscriptionRow
{
    public Guid ItemId { get; set; }
    public string? ProviderKey { get; set; }
    public bool AutoRenew { get; set; } = true;
    public LocalDate? CancelledAt { get; set; }
    public LocalDate? AnniversaryOn { get; set; }
    public int? NoticeCount { get; set; }
    public string? NoticeUnit { get; set; }
    public LocalDate? CommitmentEndsOn { get; set; }
    public LocalDate? CancellableFrom { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
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
    public Guid? ImportedBy { get; set; }
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
    public Guid? ImportedBy { get; set; }
    public List<GrantRow> Grants { get; set; } = [];

    /// <summary>
    /// Not stored: the items of the grants a replacement removed in an earlier save, so the purchase's own update
    /// line is also under them (1F3b spec §3.3, §6: the old value stays in the history of every item it fed).
    /// </summary>
    public IReadOnlyCollection<Guid> ReplacedGrantItems { get; set; } = [];
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

/// <summary>Design §3.1. Keyed by the catalog key; the one local label is <c>other</c>.</summary>
public sealed class LabelRow
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public string Origin { get; set; } = "catalog";
}

/// <summary>Design §3.2. Never deleted: a provider leaving the catalog is archived, and its subscriptions keep it.</summary>
public sealed class ProviderRow
{
    public string Key { get; set; } = "";
    public string Origin { get; set; } = "catalog";
    public string Name { get; set; } = "";
    public string? Website { get; set; }
    public string? PricingPage { get; set; }
    public List<string> LabelKeys { get; set; } = [];
    public string? CatalogCommit { get; set; }
    public Guid? CreatedBy { get; set; }
    public Instant? ArchivedAt { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public string? IconUrl { get; set; }
    public string? IconAsset { get; set; }
    public string? IconSource { get; set; }
    public uint Version { get; set; }
}

/// <summary>Design §3.6: one row per sync, the report as JSON once it succeeded.</summary>
public sealed class CatalogSyncRunRow
{
    public Guid Id { get; set; }
    public Instant StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public string Commit { get; set; } = "";
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string? Report { get; set; }
}
