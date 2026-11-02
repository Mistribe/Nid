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

public interface IFxRateStore
{
    /// <summary>Writes by priority (<see cref="FxRateSource"/>); returns the rows inserted or replaced.</summary>
    Task<int> WriteAsync(IReadOnlyList<FxRate> rates, CancellationToken ct);

    /// <summary>Every stored rate of <paramref name="quotes"/> with <c>from ≤ on_date ≤ to</c>.</summary>
    Task<IReadOnlyList<FxRate>> ReadAsync(IReadOnlyCollection<Currency> quotes, LocalDate from, LocalDate to, CancellationToken ct);

    /// <summary>For each quote, the latest stored rate strictly before <paramref name="date"/> (plausibility, design §4.4).</summary>
    Task<IReadOnlyDictionary<Currency, decimal>> PreviousAsync(IReadOnlyCollection<Currency> quotes, LocalDate date, CancellationToken ct);

    /// <summary>The dates on or after <paramref name="from"/> holding at least one rate of <paramref name="source"/>.</summary>
    Task<IReadOnlySet<LocalDate>> DatesFromAsync(FxRateSource source, LocalDate from, CancellationToken ct);
}

public enum FxRunKind
{
    Daily,
    Backfill,
}

public enum FxRunStatus
{
    Running,
    Succeeded,
    Failed,
}

public sealed record FxSyncRun(
    Guid Id,
    FxRunKind Kind,
    Instant StartedAt,
    Instant? FinishedAt,
    FxRunStatus Status,
    int FilesRead,
    int RowsWritten,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<FxRejection> Rejected)
{
    // The compiler-generated equality would compare Rejected by reference (IReadOnlyList<T> has none of its own):
    // two runs read back from storage would never equal one another. Compare it by content instead.
    public bool Equals(FxSyncRun? other)
        => other is not null
           && Id == other.Id && Kind == other.Kind && StartedAt == other.StartedAt && FinishedAt == other.FinishedAt
           && Status == other.Status && FilesRead == other.FilesRead && RowsWritten == other.RowsWritten
           && FailureCode == other.FailureCode && FailureMessage == other.FailureMessage
           && Rejected.SequenceEqual(other.Rejected);

    public override int GetHashCode()
        => HashCode.Combine(Id, Kind, StartedAt, FinishedAt, Status, FilesRead, HashCode.Combine(RowsWritten, FailureCode, FailureMessage));
}

/// <summary>The collection journal, <c>core.fx_sync_run</c> (design §4.2).</summary>
public interface IFxRunLog
{
    /// <summary>
    /// Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row.
    /// A run still marked running an hour after it started is closed as <see cref="FxCodes.RunAbandoned"/> first,
    /// so a crash never blocks collection for ever.
    /// </summary>
    Task<FxSyncRun?> TryStartAsync(FxRunKind kind, CancellationToken ct);

    Task FinishAsync(FxSyncRun run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>Whether a run of <paramref name="kind"/> has ever succeeded (the purge keeps the latest succeeded backfill).</summary>
    Task<bool> HasSucceededAsync(FxRunKind kind, CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<FxSyncRun>> RecentAsync(int count, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>, except the latest succeeded backfill (<see cref="HasSucceededAsync"/>).</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
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

/// <summary>A source file's bytes, or <c>null</c> when SubTracker has no file that day (an HTTP 404; the ECB history never is).</summary>
public sealed record FxFetch(byte[]? Bytes);

/// <summary>The two rate sources (design §4.3). Failures carry <see cref="FxCodes"/> source codes; nothing throws.</summary>
public interface IFxSourceClient
{
    Task<UnambitiousFx.Functional.Result<FxFetch>> SubTrackerDayAsync(LocalDate date, CancellationToken ct);

    Task<UnambitiousFx.Functional.Result<FxFetch>> EcbHistoryAsync(CancellationToken ct);
}

/// <summary>Starts the backfill job now (Quartz in the host); fails with <see cref="FxCodes.JobsDisabled"/> when jobs are off.</summary>
public interface IFxJobTrigger
{
    Task<UnambitiousFx.Functional.Result> TriggerBackfillAsync(CancellationToken ct);
}
