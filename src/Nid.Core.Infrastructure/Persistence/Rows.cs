using NodaTime;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class HouseholdSettingsRow
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "Nid";
    public string ReferenceCurrency { get; set; } = "EUR";
    public string Timezone { get; set; } = "Europe/Paris";
    public string Locale { get; set; } = "fr-FR";
}

public sealed class PersonRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string? OidcSubject { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public int SessionEpoch { get; set; }
    public string Preferences { get; set; } = "{}";
    public Instant CreatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class ItemRow
{
    public Guid Id { get; set; }
    public string Module { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Visibility { get; set; } = "shared";
    public string? Currency { get; set; }
    public Instant? ArchivedAt { get; set; }
    public Instant CreatedAt { get; set; }
    public uint Version { get; set; }
    public List<OwnershipRow> Owners { get; set; } = [];
}

public sealed class OwnershipRow
{
    public Guid ItemId { get; set; }
    public Guid PersonId { get; set; }
    public int ShareBp { get; set; }
}

public sealed class FxRateRow
{
    public LocalDate OnDate { get; set; }
    public string Quote { get; set; } = "";
    public decimal Rate { get; set; }
    public string Source { get; set; } = "";
    public Instant FetchedAt { get; set; }
}

public sealed class FxSyncRunRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Instant StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public int FilesRead { get; set; }
    public int RowsWritten { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string Rejected { get; set; } = "[]";
}
