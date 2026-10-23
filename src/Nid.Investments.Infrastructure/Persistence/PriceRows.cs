using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

/// <summary>investments.security_listing (holding prices spec §2.3).</summary>
public sealed class SecurityListingRow
{
    public string Isin { get; set; } = "";
    public string? BoursoramaPath { get; set; }
    public string? YahooSymbol { get; set; }
    public Instant ResolvedAt { get; set; }
    public Instant? NotFoundAt { get; set; }
}

/// <summary>investments.security_price (spec §2.4).</summary>
public sealed class SecurityPriceRow
{
    public string Isin { get; set; } = "";
    public LocalDate OnDate { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "";
    public string Source { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal? Previous { get; set; }
    public Instant FetchedAt { get; set; }
    public Guid? AcceptedBy { get; set; }
}

/// <summary>investments.price_run (spec §2.5).</summary>
public sealed class PriceRunRow
{
    public Guid Id { get; set; }
    public Instant StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public int Queried { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int NotFound { get; set; }
    public int SourceErrors { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
}
