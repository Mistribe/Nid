using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public static class InflationCodes
{
    public const string SourceUnavailable = "core.cpi_source_unavailable";
    public const string SourceMalformed = "core.cpi_source_malformed";
    public const string SourceRefused = "core.cpi_source_refused";
    public const string SyncRunning = "core.cpi_sync_running";
    public const string Disabled = "core.cpi_disabled";
    public const string JobsDisabled = "core.cpi_jobs_disabled";
    public const string FromInsee = "core.cpi_from_insee";
    public const string InvalidValue = "core.invalid_cpi_value";
}

/// <summary>
/// The observed monthly CPI, shared by every "real" figure. Fractions are exact (0.02 = 2 %); rounding belongs to the views.
/// A missing month gives null, never a guess.
/// </summary>
public sealed class InflationSeries(IReadOnlyDictionary<YearMonth, decimal> source)
{
    // A copy, so a later change to the caller's dictionary cannot move the series.
    private readonly Dictionary<YearMonth, decimal> index = new(source);

    public static readonly InflationSeries Empty = new(new Dictionary<YearMonth, decimal>());

    public YearMonth? LastPublished { get; } = source.Count == 0 ? null : source.Keys.Max();

    public decimal? Index(YearMonth month) => index.TryGetValue(month, out var v) ? v : null;

    /// <summary>CPI(to) ÷ CPI(from), null when either month is unknown.</summary>
    public decimal? Between(YearMonth from, YearMonth to) => Index(from) is { } a && Index(to) is { } b ? b / a : null;

    /// <summary>The change over the twelve months ending at the last stored month.</summary>
    public (decimal Rate, YearMonth To)? Last12Months =>
        LastPublished is { } to && Between(to.PlusMonths(-12), to) is { } r ? (r - 1, to) : null;

    /// <summary>Annual average of <paramref name="year"/> over that of the year before; null unless all 24 months are known.</summary>
    public decimal? Annual(int year) => Mean(year) is { } m && Mean(year - 1) is { } p ? m / p - 1 : null;

    /// <summary>(1 + nominal) ÷ (1 + inflation) − 1.</summary>
    public static decimal Real(decimal nominal, decimal inflation) => (1 + nominal) / (1 + inflation) - 1;

    private decimal? Mean(int year)
    {
        var months = Enumerable.Range(1, 12).Select(m => Index(new YearMonth(year, m))).ToList();
        return months.All(v => v is not null) ? months.Average(v => v!.Value) : null;
    }
}

public interface IInflation
{
    Task<InflationSeries> ReadAsync(CancellationToken ct);
}

/// <summary>Fetches the raw SDMX document of the INSEE series through the outbound client; never throws, a failure is an <see cref="InflationCodes"/> code.</summary>
public interface ICpiSourceClient
{
    Task<Result<byte[]>> FetchAsync(CancellationToken ct);
}

/// <summary>Starts the <c>cpi-weekly</c> job now (Quartz in the host); <see cref="InflationCodes.JobsDisabled"/> when jobs are off.</summary>
public interface ICpiJobTrigger
{
    Task<Result> TriggerAsync(CancellationToken ct);
}

/// <summary>CPI collection settings: bound from the <c>Inflation</c> section by the host, defaults otherwise.</summary>
public sealed class InflationOptions
{
    public string WeeklyCron { get; set; } = "0 0 7 ? * MON";

    /// <summary>INSEE BDM 011814630 (IPC base 2025, monthly), the whole series.</summary>
    public string SourceUrl { get; set; } = "https://bdm.insee.fr/series/sdmx/data/SERIES_BDM/011814630";
}

/// <summary>A stored CPI value as the admin page shows it: the month as <c>2026-09</c>, the value as a decimal string.</summary>
public sealed record CpiValueView(string Month, string Value, LocalDate? PublishedOn, LocalDate FetchedOn, string Source);

public sealed record CpiAdminView(IReadOnlyList<CpiValueView> Values, IReadOnlyList<CpiRunView> Runs);

public sealed record CpiSyncRequested(bool Requested);

/// <summary>One collection of the CPI series, <c>core.cpi_run</c>; <c>Status</c> is <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record CpiRunView(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, int Added, int Replaced, int Rejected, string? Message);

public interface ICpiRunLog
{
    /// <summary>Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row. A run still running an hour on is closed as failed first.</summary>
    Task<CpiRunView?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(CpiRunView run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<CpiRunView>> RecentAsync(int count, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}
