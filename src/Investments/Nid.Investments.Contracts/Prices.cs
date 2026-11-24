namespace Nid.Investments.Contracts;

/// <summary>The wire values of <c>security_price.source</c>.</summary>
public static class PriceSources
{
    public const string Boursorama = "boursorama";
    public const string Yahoo = "yahoo";
}

public static class PriceCodes
{
    public const string SourceUnavailable = "investments.price_source_unavailable";
    public const string SourceMalformed = "investments.price_source_malformed";
    public const string SourceRefused = "investments.price_source_refused";
    public const string SyncRunning = "investments.price_sync_running";
    public const string Disabled = "investments.prices_disabled";
    public const string RunInterrupted = "investments.price_run_interrupted";
    public const string RunAbandoned = "investments.price_run_abandoned";
    public const string JobsDisabled = "investments.price_jobs_disabled";
    public const string NotRejected = "investments.price_not_rejected";
}

/// <summary>Holding prices settings (spec §2): bound from configuration by the host, defaults otherwise.</summary>
public sealed class PriceOptions
{
    public string DailyCron { get; set; } = "0 0 21 ? * MON-FRI";

    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromSeconds(1);

    public decimal MaxChange { get; set; } = 0.20m;

    public int MaxAgeDays { get; set; } = 10;

    public int NotFoundRetryDays { get; set; } = 7;

    public string BoursoramaBaseUrl { get; set; } = "https://www.boursorama.com";

    public string YahooBaseUrl { get; set; } = "https://query2.finance.yahoo.com";
}
