using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>
/// Where a stored rate comes from. The numeric value is the write priority (design §4.1): a write never replaces a
/// row of higher priority, and replaces a row of equal priority (a corrected upstream file wins).
/// </summary>
public enum FxRateSource
{
    Ecb = 0,
    SubTrackerData = 1,
    Manual = 2,
}

public static class FxRateSourceNames
{
    public static string ToWire(this FxRateSource source) => source switch
    {
        FxRateSource.Ecb => "ecb",
        FxRateSource.SubTrackerData => "subtracker_data",
        FxRateSource.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static FxRateSource FromWire(string source) => source switch
    {
        "ecb" => FxRateSource.Ecb,
        "subtracker_data" => FxRateSource.SubTrackerData,
        "manual" => FxRateSource.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown rate source."),
    };
}

/// <summary>Units of <see cref="Quote"/> for 1 USD (<c>core-data-model.md</c> §8.2). USD itself is never stored.</summary>
public sealed record FxRate(LocalDate OnDate, Currency Quote, decimal Rate, FxRateSource Source);

/// <summary>A rate read from a source and not written, with why (design §4.4). <paramref name="Quote"/> is raw text: it may not be a valid code.</summary>
public sealed record FxRejection(LocalDate OnDate, string Quote, decimal Rate, decimal? Previous, string Reason);

public static class FxCodes
{
    public const string RateMissing = "core.fx_rate_missing";
    public const string InvalidRate = "core.fx_invalid_rate";
    public const string InvalidRange = "core.fx_invalid_range";
    public const string SourceUnavailable = "core.fx_source_unavailable";
    public const string SourceMalformed = "core.fx_source_malformed";
    public const string SourceRefused = "core.fx_source_refused";
    public const string SyncRunning = "core.fx_sync_running";
    public const string JobsDisabled = "core.fx_jobs_disabled";
    public const string RunAbandoned = "core.fx_run_abandoned";
    public const string RunInterrupted = "core.fx_run_interrupted";

    /// <summary>Reasons of an <see cref="FxRejection"/>: data, not failures.</summary>
    public const string ImplausibleChange = "implausible_change";
    public const string OutOfRange = "out_of_range";
    public const string InvalidCode = "invalid_code";
    public const string InvalidValue = "invalid_value";
}

/// <summary>Bound from the <c>Fx</c> configuration section by the host; the defaults are the design's.</summary>
public sealed class FxOptions
{
    /// <summary>Beyond this many days without a rate, a conversion is <see cref="FxCodes.RateMissing"/> (design §5.1).</summary>
    public int MaxGapDays { get; set; } = 7;

    /// <summary>Relative daily change above which a collected rate is refused (design §4.4): 0.25 = 25 %.</summary>
    public decimal MaxDailyChange { get; set; } = 0.25m;

    /// <summary>Ends with '/': a day's file is <c>{yyyy}/{MM}/{dd}.json</c> under it.</summary>
    public string SubTrackerBaseUrl { get; set; } = "https://raw.githubusercontent.com/Mistribe/SubTracker-Data/main/exchange/";

    public string EcbHistoryUrl { get; set; } = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.zip";

    /// <summary>Quartz cron (seconds first), UTC: after the upstream commit, scheduled at 01:00 UTC but landing up to 07:10 UTC.</summary>
    public string DailyCron { get; set; } = "0 0 9 * * ?";

    /// <summary>The first SubTracker-Data file (checked 2026-09-23): nothing before it is ever fetched.</summary>
    public LocalDate SubTrackerStart { get; init; } = new(2025, 7, 30);
}

public interface IFxRates
{
    /// <summary>
    /// The rates of <paramref name="quotes"/> (USD ignored) from <paramref name="from"/> minus the maximum gap to the
    /// later of <paramref name="to"/> and today, so every date in the range and every projection can be looked up.
    /// </summary>
    Task<RateTable> LoadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct);
}
