using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>The region codes of a breakdown, in display order (holdings exposure spec D1).</summary>
public static class BreakdownRegions
{
    public static readonly IReadOnlyList<string> All =
        ["north_america", "europe_ex_france", "france", "japan", "asia_pacific_ex_japan", "emerging", "other"];
}

/// <summary>The sector codes of a breakdown, in display order (holdings exposure spec D2).</summary>
public static class BreakdownSectors
{
    public static readonly IReadOnlyList<string> All =
    [
        "information_technology", "health_care", "financials", "industrials", "consumer_discretionary", "consumer_staples", "energy",
        "materials", "communication_services", "utilities", "real_estate", "other"
    ];
}

public static class BreakdownCodes
{
    public const string Not100 = "investments.breakdown_not_100";
    public const string NotMarket = "investments.breakdown_not_market";
    public const int StaleAfterDays = 365;
}

public sealed record BreakdownPart(string Code, decimal Pct);

public sealed record SecurityBreakdown(
    Guid Id, string Isin, LocalDate AsOf, IReadOnlyList<BreakdownPart> Regions, IReadOnlyList<BreakdownPart>? Sectors,
    Guid? DocumentId, PersonId RecordedBy, Instant RecordedAt);

public interface ISecurityBreakdownStore
{
    /// <summary>Per ISIN, its most recent breakdown; an ISIN without one is absent.</summary>
    Task<IReadOnlyDictionary<string, SecurityBreakdown>> LatestAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Every version of these ISINs, oldest first (export).</summary>
    Task<IReadOnlyList<SecurityBreakdown>> AllAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Creates it or replaces the one of the same ISIN and date (keeping that row's id); returns the row's id.</summary>
    Task<Guid> WriteAsync(SecurityBreakdown breakdown, CancellationToken ct);

    Task<bool> DeleteAsync(string isin, LocalDate asOf, CancellationToken ct);

    /// <summary>Sets the source document on the breakdown rows among <paramref name="ids"/>; other ids are ignored.</summary>
    Task StampDocumentAsync(Guid documentId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
}
